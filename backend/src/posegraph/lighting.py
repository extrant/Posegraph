"""照片估光：从照片里人物的脸估出主光从哪来、多硬、冷暖和光比，给插件的「照片同款」灯光方案用。

做法：面捕（face.py）找到的 468 个脸部点当一张脸的网格，在脸部裁剪图上按像素取法线和亮度，
只用皮肤（颜色落在脸部主色附近，去掉眼睛、眉毛、嘴、头发、高光），拟合「环境光 + 1~2 盏方向光」：
    亮度 = 环境光 + Σ 强度 × max(0, (法线·光方向 + 柔和度) / (1 + 柔和度))
再把光方向换成灯光方案的方位 / 仰角（相对脸和镜头，见插件 LightRig.cs）。

有脸时只看脸：身体的网格和照片对不齐（尤其手臂、手），SAM 的头部俯仰也常有偏差，混进来会把光估成从下往上照。
没有脸（背对、没拍到头、脸太小）时改用身体上露出的皮肤（estimate_body）：SAM 网格的法线，躯干对得比手臂准；
皮肤只有一种底色，衣服各有各的底色分不开明暗，所以只用皮肤，露出的皮肤太少就不估。光的方向对整个画面是同一个，
脸上估的、身上估的都能用来摆全身的灯。
皮肤也不够时强制估（_coarse）：衣服按颜色分几大块，每块一个底色，只看同一块里的受光，结果只当个大概。
光比接近 1:1（正面闪光灯、大面积柔光）时方向没有意义，标成 flat，插件换成正面柔光。
色温只能分冷暖：皮肤本身的颜色和灯的颜色分不开，按常见照片里的肤色当基准。
照片配色：每盏灯、环境光（拟合里的常数项，多是周围环境反射来的光；暗部的皮肤因为透光偏红，这个颜色偏暖，只当备用）
和背景（人以外的像素）的颜色，
离色温线远的（彩色灯、霓虹、彩色背景）标 tinted，插件直接用 RGB，否则用色温。
"""

from __future__ import annotations

import functools

import numpy as np

FACE_SIDE = 320          # 脸部裁剪缩放到脸宽约这么多像素再取样
MAX_ZOOM = 4.0
MIN_PIXELS = 1200        # 用得上的皮肤像素少于这么多就不估（脸太小、被手或墨镜挡住太多）
MAX_SAMPLES = 15000
FLAT_RATIO = 1.5         # 左右脸光比低于这个当平光
BODY_MIN_SKIN = 0.25     # 用身体估光：露出的皮肤至少占人（网格投影）的这么多
BODY_MIN_PIXELS = 3000   # 且至少这么多像素
# 身体估光的常见肤色粗筛（OpenCV Lab 减去 128 后的 a*、b*，色相角度）：红衣服 a* 太大、牛仔布色相不对，筛得掉
SKIN_A, SKIN_B, SKIN_HUE, SKIN_CHROMA, SKIN_L = (5, 32), (6, 42), (35, 75), 9, 25
HAND_KEYPOINTS = range(21, 63)   # SAM 关键点里的两只手：网格的手指和照片对不齐，不用
COARSE_GROUPS = 6        # 强制估：人身上按颜色分这么多块
COARSE_LIGHTNESS = 0.15  # 分块时亮度的分量（相对色相）：小了黑白衣服分不开，大了同一件衣服的明暗两面会被拆开
COARSE_MIN_GROUP = 150   # 太小的块不用
COARSE_MIN_PIXELS = 2000
SECOND_GAIN = 0.8        # 两盏灯的拟合误差要比一盏小 20% 以上、第二盏亮度至少是主光的 15% 才用两盏
SECOND_MIN_POWER = 0.15
TINTED = 0.15            # 颜色离最近的色温超过这么多（最大分量归一后的距离）算彩色
MIN_BACKGROUND = 0.02    # 背景像素少于整张图的这么多就不算背景色（人占满了画面）
MIN_AMBIENT = 0.05       # 环境光不到主光的这么多时不给它的颜色（系数接近 0，颜色全是噪声）
# 白平衡正常的照片里皮肤在白光下的线性 RGB 比例（按测试照片的中位数校准到约 5500K）
SKIN = np.array([1.0, 0.752, 0.766])

# 不算皮肤的部位（MediaPipe 脸部点编号）：两眼、两眉、嘴唇
HOLES = [[33, 7, 163, 144, 145, 153, 154, 155, 133, 173, 157, 158, 159, 160, 161, 246],
         [263, 249, 390, 373, 374, 380, 381, 382, 362, 398, 384, 385, 386, 387, 388, 466],
         [70, 63, 105, 66, 107, 55, 65, 52, 53, 46], [300, 293, 334, 296, 336, 285, 295, 282, 283, 276],
         [61, 146, 91, 181, 84, 17, 314, 405, 321, 375, 291, 409, 270, 269, 267, 0, 37, 39, 40, 185]]


@functools.lru_cache(maxsize=1)
def face_triangles() -> np.ndarray:
    """MediaPipe 脸网格的三角形：官方只给了边（FACE_LANDMARKS_TESSELATION），三条边两两相连的就是三角形。"""
    import collections

    from mediapipe.tasks.python.vision.face_landmarker import FaceLandmarksConnections

    adj = collections.defaultdict(set)
    for c in FaceLandmarksConnections.FACE_LANDMARKS_TESSELATION:
        adj[c.start].add(c.end)
        adj[c.end].add(c.start)
    tris = {tuple(sorted((a, b, c))) for a in adj for b in adj[a] for c in adj[a] & adj[b]}
    return np.array(sorted(tris), dtype=np.int32)


def srgb_to_linear(c: np.ndarray) -> np.ndarray:
    c = c.astype(np.float32) / 255
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def kelvin_to_rgb(kelvin: float) -> np.ndarray:
    """色温 → 线性 RGB（最大分量 1），和插件 LightRigs.KelvinToRgb 同一个近似公式。"""
    t = float(np.clip(kelvin, 1000, 40000)) / 100
    if t <= 66:
        r, g = 255.0, 99.4708025861 * np.log(t) - 161.1195681661
        b = 0.0 if t <= 19 else 138.5177312231 * np.log(t - 10) - 305.0447927307
    else:
        r, g, b = 329.698727446 * (t - 60) ** -0.1332047592, 288.1221695283 * (t - 60) ** -0.0755148492, 255.0
    c = np.clip(np.array([r, g, b]), 0, 255) / 255
    c = c ** 2.2
    return c / c.max()


_KELVINS = np.arange(2000, 12001, 100)
_KELVIN_RGB = np.array([kelvin_to_rgb(k) for k in _KELVINS])


def to_kelvin(rgb: np.ndarray) -> int:
    """最接近这个颜色的色温（2000K～12000K）。"""
    c = np.asarray(rgb, float) / max(float(np.max(rgb)), 1e-9)
    return int(_KELVINS[int(np.argmin(np.linalg.norm(_KELVIN_RGB - c, axis=1)))])


def describe_color(rgb) -> dict | None:
    """线性 RGB → {kelvin, rgb（最大分量 1）, tinted}；全黑为 None。"""
    rgb = np.asarray(rgb, float)
    if rgb.max() <= 1e-6:
        return None
    c = rgb / rgb.max()
    kelvin = to_kelvin(c)
    return {"kelvin": kelvin, "rgb": [round(float(v), 3) for v in c],
            "tinted": bool(np.linalg.norm(c - kelvin_to_rgb(kelvin)) > TINTED)}


def background_color(image: np.ndarray, background: np.ndarray | None, skin_luma: float) -> dict | None:
    """背景（background 为 True 的像素）的颜色和相对脸的亮度（背景中位亮度 / 脸上皮肤中位亮度）。"""
    if background is None or background.sum() < MIN_BACKGROUND * background.size:
        return None
    linear = srgb_to_linear(image[background])
    color = describe_color(np.median(linear, 0))
    if color is None:
        return {"kelvin": 5500, "rgb": [1.0, 1.0, 1.0], "tinted": False, "brightness": 0.0}
    luma = float(np.median(linear @ np.array([0.2126, 0.7152, 0.0722])))
    return {**color, "brightness": round(luma / max(skin_luma, 1e-4), 3)}


def shade(normals: np.ndarray, ambient: float, lights) -> np.ndarray:
    out = np.full(len(normals), ambient, np.float64)
    for power, direction, wrap in lights:
        out += power * np.maximum(0, (normals @ direction + wrap) / (1 + wrap))
    return out


def _unit(theta: float, phi: float) -> np.ndarray:
    return np.array([np.sin(theta) * np.cos(phi), np.sin(theta) * np.sin(phi), np.cos(theta)])


def fit(normals: np.ndarray, values: np.ndarray, count: int):
    """拟合 count 盏灯。返回 (环境光, [(强度, 方向, 柔和度)] 按强度从大到小, 误差)。从球面 26 个方向起步各拟合一次取最好。"""
    from scipy.optimize import least_squares

    starts = [np.array(d, float) / np.linalg.norm(d) for d in
              [(x, y, z) for x in (-1, 0, 1) for y in (-1, 0, 1) for z in (-1, 0, 1) if (x, y, z) != (0, 0, 0)]]
    scale = float(np.median(values)) or 0.1
    rng = np.random.default_rng(0)

    def unpack(p):
        return p[0], [(p[1 + 4 * i], _unit(p[2 + 4 * i], p[3 + 4 * i]), p[4 + 4 * i]) for i in range(count)]

    def residual(p):
        a, ls = unpack(p)
        return shade(normals, a, ls) - values

    best = None
    for d in starts:
        dirs = [d] + [starts[j] for j in rng.choice(len(starts), count - 1)]
        p0 = [scale * 0.2]
        for v in dirs:
            p0 += [scale, float(np.arccos(np.clip(v[2], -1, 1))), float(np.arctan2(v[1], v[0])), 0.2]
        lo = [0.0] + [0.0, -np.inf, -np.inf, 0.0] * count
        hi = [np.inf] + [np.inf, np.inf, np.inf, 1.5] * count
        r = least_squares(residual, p0, bounds=(lo, hi), loss="soft_l1", f_scale=scale * 0.1, max_nfev=200)
        if best is None or r.cost < best.cost:
            best = r
    a, ls = unpack(best.x)
    ls.sort(key=lambda light: -light[0])
    return a, ls, float(best.cost)


def _raster(uv: np.ndarray, tris: np.ndarray, depth: np.ndarray, shape) -> tuple[np.ndarray, ...]:
    """三角形按深度从远到近画编号（画家算法），返回每个像素的 (行, 列, 三角形, 重心坐标)。"""
    import cv2

    ids = np.full(shape, -1, np.float32)
    pts = np.round(uv[tris] * 16).astype(np.int32)
    for i in np.argsort(-depth):
        cv2.fillPoly(ids, [pts[i]], float(i), lineType=cv2.LINE_8, shift=4)
    ids = ids.astype(np.int64)
    yy, xx = np.nonzero(ids >= 0)
    t = ids[yy, xx]
    a, b, c = uv[tris[t, 0]], uv[tris[t, 1]], uv[tris[t, 2]]
    p = np.stack([xx + 0.5, yy + 0.5], 1)
    v0, v1, v2 = b - a, c - a, p - a
    d00, d01, d11 = (v0 * v0).sum(1), (v0 * v1).sum(1), (v1 * v1).sum(1)
    d20, d21 = (v2 * v0).sum(1), (v2 * v1).sum(1)
    den = d00 * d11 - d01 * d01
    den[np.abs(den) < 1e-9] = 1e-9
    bv = (d11 * d20 - d01 * d21) / den
    bw = (d00 * d21 - d01 * d20) / den
    return yy, xx, t, np.clip(np.stack([1 - bv - bw, bv, bw], 1), 0, 1)


def face_normals(points: np.ndarray, ray: np.ndarray, shape) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """脸部点（裁剪图像素 x、y，z 同尺度、越小越近）当网格画出来，返回每个像素的 (行, 列, 法线)，法线在相机系（x 右 y 下 z 前）。
    MediaPipe 的点是正交投影：把它的视线（+z）转到镜头看脸的那条射线 ray 上（脸不在画面正中时差十几度）。"""
    from scipy.spatial.transform import Rotation

    p = np.asarray(points, float)[:468]
    tris = face_triangles().copy()
    fn = np.cross(p[tris[:, 1]] - p[tris[:, 0]], p[tris[:, 2]] - p[tris[:, 0]])
    center = p.mean(0) + [0, 0, np.ptp(p[:, 0]) * 0.6]   # 头的中心大致在脸后面半张脸宽
    flip = np.sum(fn * (p[tris].mean(1) - center), 1) < 0
    tris[flip] = tris[flip][:, ::-1]
    fn[flip] = -fn[flip]
    vn = np.zeros_like(p)
    for k in range(3):
        np.add.at(vn, tris[:, k], fn)
    vn /= np.linalg.norm(vn, axis=1, keepdims=True) + 1e-12
    r = ray / np.linalg.norm(ray)
    axis = np.cross([0.0, 0.0, 1.0], r)
    if np.linalg.norm(axis) > 1e-6:
        vn = Rotation.from_rotvec(axis / np.linalg.norm(axis) * np.arccos(np.clip(r[2], -1, 1))).apply(vn)
    yy, xx, t, bary = _raster(p[:, :2], tris, p[tris].mean(1)[:, 2], shape)
    n = (vn[tris[t]] * bary[:, :, None]).sum(1)
    return yy, xx, n / (np.linalg.norm(n, axis=1, keepdims=True) + 1e-12)


def skin_mask(crop: np.ndarray, seed: np.ndarray) -> np.ndarray:
    """种子区域里真正是皮肤的像素：Lab 的 a、b 落在种子的颜色分布里（两遍，先筛掉头发、胡子再重算分布），去掉过曝和死黑。"""
    import cv2

    lab = cv2.cvtColor(crop, cv2.COLOR_RGB2LAB).astype(np.float32)[..., 1:]
    keep = seed.copy()
    for limit in (2.5, 3.0):
        x = lab[keep]
        if len(x) < 10:
            return keep
        mu, cov = np.median(x, 0), np.cov(x.T) + np.eye(2) * 4
        d = lab - mu
        keep = seed & (np.einsum("hwi,ij,hwj->hw", d, np.linalg.inv(cov), d) < limit * limit)
    lum = srgb_to_linear(crop) @ np.array([0.2126, 0.7152, 0.0722])
    return keep & (crop.max(-1) < 250) & (lum > 0.002)


def estimate(image: np.ndarray, landmarks, focal: float, image_scale: float, up=None, forward=None,
             background: np.ndarray | None = None) -> dict:
    """image：识别用的照片（RGB，可以是缩小的预览图）；landmarks：面捕的 478 个点（image 的像素）；
    focal：原图的焦距（像素），image_scale = image 宽 / 原图宽；up：相机系里的「上」（有地面时用地面法线，否则按相机竖直）；
    forward：脸朝向（相机系），用来定方位的正负（正 = 脸转过去的那一侧，和插件灯光方案一致）；
    background：和 image 一样大的布尔图，True 为背景（不是人）的像素，用来取背景色。

    返回 {found, reason, flat, ratio, ambient, ambientColor, background, lights: [{azimuth, elevation, power, softness, kelvin, rgb, tinted}]}，
    power 以主光为 1，ambient 是环境光相对主光的强度，softness 0～1.5（越大光越柔、明暗交界越宽）；
    颜色见 describe_color，background 另有 brightness（背景亮度 / 脸的亮度），没有背景时为 None。"""
    import cv2

    lm = np.asarray(landmarks, float)
    h, w = image.shape[:2]
    x0, y0 = np.maximum(lm[:, :2].min(0) - 4, 0).astype(int)
    x1, y1 = np.minimum(np.ceil(lm[:, :2].max(0) + 4), [w, h]).astype(int)
    if x1 - x0 < 8 or y1 - y0 < 8:
        return {"found": False, "reason": "脸太小"}
    zoom = min(MAX_ZOOM, FACE_SIDE / (x1 - x0))
    crop = cv2.resize(np.ascontiguousarray(image[y0:y1, x0:x1, :3]), (round((x1 - x0) * zoom), round((y1 - y0) * zoom)),
                      interpolation=cv2.INTER_CUBIC if zoom > 1 else cv2.INTER_AREA)
    pts = (lm - [x0, y0, 0]) * zoom
    center = lm[:468, :2].mean(0) / image_scale
    full_w, full_h = w / image_scale, h / image_scale
    ray = np.array([(center[0] - full_w / 2) / focal, (center[1] - full_h / 2) / focal, 1.0])
    yy, xx, normals = face_normals(pts, ray, crop.shape[:2])

    seed = np.zeros(crop.shape[:2], np.uint8)
    seed[yy, xx] = 1
    seed = cv2.erode(seed, np.ones((3, 3), np.uint8), iterations=2)
    for hole in HOLES:
        m = np.zeros_like(seed)
        cv2.fillPoly(m, [cv2.convexHull(np.round(pts[hole, :2]).astype(np.int32))], 1)
        seed[cv2.dilate(m, np.ones((9, 9), np.uint8)) > 0] = 0
    skin = skin_mask(crop, seed.astype(bool))
    nimg = np.zeros(crop.shape[:2] + (3,))
    nimg[yy, xx] = normals
    sy, sx = np.nonzero(skin)
    if len(sy) < MIN_PIXELS:
        return {"found": False, "reason": "脸上能看清的皮肤太少（脸太小或被挡住）"}
    if len(sy) > MAX_SAMPLES:
        pick = np.random.default_rng(1).choice(len(sy), MAX_SAMPLES, replace=False)
        sy, sx = sy[pick], sx[pick]
    n = nimg[sy, sx]
    rgb = srgb_to_linear(crop)[sy, sx]
    return {**_solve(n, rgb, ray, up, forward, image, background), "source": "face"}


def _solve(n: np.ndarray, rgb: np.ndarray, ray: np.ndarray, up, forward, image: np.ndarray, background) -> dict:
    """皮肤像素的法线 n（相机系）和线性 RGB → 灯（见 estimate 的返回值）。ray：镜头看被照的人的射线（相机系）。"""
    from scipy.optimize import nnls

    lum = rgb @ np.array([0.2126, 0.7152, 0.0722])
    ambient, lights, cost = fit(n, lum, 1)
    a2, l2, cost2 = fit(n, lum, 2)
    if cost2 < cost * SECOND_GAIN and l2[1][0] > SECOND_MIN_POWER * l2[0][0]:
        ambient, lights = a2, l2
    basis = np.stack([np.ones(len(n))] + [np.maximum(0, (n @ d + s) / (1 + s)) for _, d, s in lights], 1)
    colors = np.array([nnls(basis, rgb[:, c])[0] for c in range(3)]).T / SKIN   # 每行：环境光、各盏灯的 RGB
    return _describe(ambient, lights, colors, ray, up, forward, image, background, float(np.median(lum)))


def _describe(ambient: float, lights, colors: np.ndarray | None, ray: np.ndarray, up, forward, image: np.ndarray, background,
              reference_luma: float) -> dict:
    """拟合出的灯（相机系方向）→ 灯光方案用的方位 / 仰角 / 颜色，加上光比、环境光、背景色。colors 为 None 时灯都当白光。"""
    up = np.array([0.0, -1.0, 0.0]) if up is None else np.asarray(up, float)
    up = up / np.linalg.norm(up)
    flat = lambda v: (v - (v @ up) * up) / (np.linalg.norm(v - (v @ up) * up) + 1e-9)
    to_camera = flat(-ray)
    right = np.cross(up, to_camera)
    side = -1.0 if forward is not None and flat(np.asarray(forward, float)) @ right < -0.05 else 1.0
    key = lights[0][0]
    white = {"kelvin": 5500, "rgb": [1.0, 1.0, 1.0], "tinted": False}
    out = []
    for i, (power, d, soft) in enumerate(lights):
        horizontal = d - (d @ up) * up
        color = (describe_color(colors[1 + i]) if colors is not None else None) or white
        out.append({"azimuth": round(float(np.degrees(np.arctan2(horizontal @ right, horizontal @ to_camera))) * side, 1),
                    "elevation": round(float(np.degrees(np.arcsin(np.clip(d @ up, -1, 1)))), 1),
                    "power": round(float(power / key), 3), "softness": round(float(soft), 3), **color})
    # 光比：拟合出的光照在脸左右各 45° 的两个面上的亮度比
    sides = [shade(np.array([[s * 0.7071, 0, -0.7071]]), ambient, lights)[0] for s in (1, -1)]
    ratio = max(sides) / max(min(sides), 1e-6)
    return {"found": True, "reason": "", "flat": bool(ratio < FLAT_RATIO or lights[0][2] > 0.8),
            "ratio": round(float(ratio), 2), "ambient": round(float(ambient / key), 3), "lights": out,
            "ambientColor": describe_color(colors[0]) if colors is not None and ambient / key >= MIN_AMBIENT else None,
            "background": background_color(image, background, reference_luma)}


def estimate_body(image: np.ndarray, vertices: np.ndarray, faces: np.ndarray, focal: float, image_scale: float,
                  keypoints_2d, joints: np.ndarray, up=None, background: np.ndarray | None = None) -> dict:
    """没有脸时看身体：露出的皮肤够多就只用皮肤（source = body）；不够时强制估（source = coarse，见 _coarse）。
    vertices：SAM 网格顶点（相机系，已加 cam_t）；joints：MHR 关节（相机系，已加 cam_t，算身体朝向和镜头看人的射线）；
    keypoints_2d：SAM 关键点（原图像素，去掉手用）。其余同 estimate，返回值也同 estimate。"""
    import cv2

    nimg, mask, person = _body_pixels(image, vertices, faces, focal, image_scale, keypoints_2d)
    forward, ray = _body_frame(joints)
    crop = np.ascontiguousarray(image[..., :3])
    lab = cv2.cvtColor(crop, cv2.COLOR_RGB2LAB).astype(np.float32)
    a, b, light = lab[..., 1] - 128, lab[..., 2] - 128, lab[..., 0]
    hue = np.degrees(np.arctan2(b, a))
    rough = mask & (a > SKIN_A[0]) & (a < SKIN_A[1]) & (b > SKIN_B[0]) & (b < SKIN_B[1]) \
        & (hue > SKIN_HUE[0]) & (hue < SKIN_HUE[1]) & (np.hypot(a, b) > SKIN_CHROMA) & (light > SKIN_L)
    skin = skin_mask(crop, rough) if rough.sum() >= 10 else rough
    if skin.sum() >= max(BODY_MIN_PIXELS, BODY_MIN_SKIN * person):
        sy, sx = _sample(skin)
        n = nimg[sy, sx]
        n /= np.linalg.norm(n, axis=1, keepdims=True) + 1e-12
        rgb = srgb_to_linear(crop)[sy, sx]
        return {**_solve(n, rgb, ray, up, forward, image, background), "source": "body"}
    coarse = _coarse(crop, nimg, mask)
    if coarse is None:
        return {"found": False, "reason": "人在照片里太小，看不清明暗"}
    ambient, lights, reference = coarse
    # 强制估只看受光的方向：衣服的明暗被底色冲淡，光比偏小，不按光比判平光（判了就换成正面柔光，方向白估）
    return {**_describe(ambient, lights, None, ray, up, forward, image, background, reference), "source": "coarse", "flat": False}


def _body_pixels(image: np.ndarray, vertices, faces, focal: float, image_scale: float, keypoints_2d):
    """SAM 网格画到照片上：每个像素的法线（相机系）、能用的像素（往里收一圈轮廓、去掉两只手），和人占的像素数。"""
    import cv2

    h, w = image.shape[:2]
    full_w, full_h = w / image_scale, h / image_scale
    v = np.asarray(vertices, float)
    z = np.maximum(v[:, 2], 1e-6)
    uv = np.stack([focal * v[:, 0] / z + full_w / 2, focal * v[:, 1] / z + full_h / 2], 1) * image_scale
    tris = np.asarray(faces, np.int64)
    tris = tris[(v[tris, 2] > 1e-3).all(1)]
    yy, xx, t, bary = _raster(uv, tris, v[tris].mean(1)[:, 2], (h, w))
    fn = np.cross(v[tris[:, 1]] - v[tris[:, 0]], v[tris[:, 2]] - v[tris[:, 0]])
    vn = np.zeros_like(v)
    for k in range(3):
        np.add.at(vn, tris[:, k], fn)
    vn /= np.linalg.norm(vn, axis=1, keepdims=True) + 1e-12
    if np.mean(np.sum(vn * (v - v.mean(0)), axis=1)) < 0:   # 三角形绕序反了：法线翻过来朝外
        vn = -vn
    nimg = np.zeros((h, w, 3))
    nimg[yy, xx] = (vn[tris[t]] * bary[:, :, None]).sum(1)
    person = np.zeros((h, w), np.uint8)
    person[yy, xx] = 1
    mask = cv2.erode(person, np.ones((3, 3), np.uint8), iterations=3)
    k2 = np.asarray(keypoints_2d, float)[:, :2] * image_scale
    shoulder = max(4.0, float(np.linalg.norm(k2[5] - k2[6])))
    for i in HAND_KEYPOINTS:
        if i < len(k2):
            cv2.circle(mask, tuple(int(c) for c in np.round(k2[i])), int(shoulder * 0.12), 0, -1)
    return nimg, mask > 0, int(person.sum())


def _body_frame(joints: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """身体朝向（相机系）和镜头看胸口的射线。MHR：+x 角色左侧、y 上、z 前；相机系同样是右手系，所以 前 = 左 × 上。"""
    from .retarget.mhr import MHR_JOINT_NAMES

    joint = lambda name: np.asarray(joints[MHR_JOINT_NAMES.index(name)], float)
    left = joint("l_uparm") - joint("r_uparm")
    body_up = joint("c_neck") - joint("root")
    return np.cross(left, body_up), (joint("c_spine2") + joint("c_neck")) / 2


def _sample(mask: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    sy, sx = np.nonzero(mask)
    if len(sy) > MAX_SAMPLES:
        pick = np.random.default_rng(1).choice(len(sy), MAX_SAMPLES, replace=False)
        sy, sx = sy[pick], sx[pick]
    return sy, sx


def _coarse(crop: np.ndarray, nimg: np.ndarray, mask: np.ndarray):
    """
    强制估（脸、皮肤都用不了时）：人身上的像素按颜色分 COARSE_GROUPS 大块（色相为主，亮度只占一点，
    同一件衣服的受光面、背光面才不会被分开），每块一个未知底色，只看同一块里朝向不同的面谁亮谁暗。
    底色给定光照时有闭式解，所以只拟合光（见 fit_groups）。黑白灰的衣服分不太开，结果只当个大概。
    返回 (环境光, 灯, 参考亮度)；人太小为 None。
    """
    import cv2

    lin = srgb_to_linear(crop)
    lum = lin @ np.array([0.2126, 0.7152, 0.0722])
    usable = mask & (crop.max(-1) < 250) & (lum > 0.003)
    py, px = np.nonzero(usable)
    if len(py) < COARSE_MIN_PIXELS:
        return None
    lab = cv2.cvtColor(crop, cv2.COLOR_RGB2LAB).astype(np.float32)
    feature = np.stack([lab[py, px, 1], lab[py, px, 2], lab[py, px, 0] * COARSE_LIGHTNESS], 1).astype(np.float32)
    cv2.setRNGSeed(1)
    _, labels, _ = cv2.kmeans(feature, COARSE_GROUPS, None, (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 30, 0.5), 3,
                              cv2.KMEANS_PP_CENTERS)
    group = np.full(crop.shape[:2], -1, np.int32)
    group[py, px] = labels.ravel()
    counts = np.bincount(labels.ravel(), minlength=COARSE_GROUPS)
    group[np.isin(group, np.flatnonzero(counts < COARSE_MIN_GROUP))] = -1
    sy, sx = _sample(group >= 0)
    n = nimg[sy, sx]
    n /= np.linalg.norm(n, axis=1, keepdims=True) + 1e-12
    y, g = lum[sy, sx], group[sy, sx]
    ambient, lights, cost = fit_groups(n, y, g, 1)
    a2, l2, cost2 = fit_groups(n, y, g, 2)
    if cost2 < cost * SECOND_GAIN and l2[1][0] > SECOND_MIN_POWER:
        ambient, lights = a2, l2
    return ambient, lights, float(np.median(y))


def fit_groups(normals: np.ndarray, values: np.ndarray, groups: np.ndarray, count: int):
    """
    每块一个底色的拟合：亮度 = 底色[块] × (环境光 + Σ 强度 × wrap(法线·方向))。参数是环境光和每盏灯的方向、柔和度、
    强度（第一盏强度固定为 1，底色吸收整体亮度），底色按给定光照闭式求（变量投影）。残差按块的平均亮度归一，深色衣服不吃亏。
    返回 (环境光, [(强度, 方向, 柔和度)]（主光强度为 1）, 误差)。
    """
    from scipy.optimize import least_squares

    ids = np.searchsorted(np.unique(groups), groups)
    norm = 1 / np.maximum((np.bincount(ids, values) / np.bincount(ids))[ids], 1e-4)

    def lights_of(p):
        return [(1.0 if i == 0 else p[4 + 4 * i], _unit(p[1 + 4 * i], p[2 + 4 * i]), p[3 + 4 * i]) for i in range(count)]

    def residual(p):
        s = shade(normals, p[0], lights_of(p))
        albedo = np.bincount(ids, values * s) / (np.bincount(ids, s * s) + 1e-9)
        return (values - albedo[ids] * s) * norm

    starts = [np.array(d, float) / np.linalg.norm(d) for d in
              [(x, y, z) for x in (-1, 0, 1) for y in (-1, 0, 1) for z in (-1, 0, 1) if (x, y, z) != (0, 0, 0)]]
    rng = np.random.default_rng(0)
    best = None
    for d in starts:
        p0 = [0.2]
        for v in [d] + [starts[j] for j in rng.choice(len(starts), count - 1)]:
            p0 += [float(np.arccos(np.clip(v[2], -1, 1))), float(np.arctan2(v[1], v[0])), 0.2, 0.5]
        lo = [0.0] + [-np.inf, -np.inf, 0.0, 0.0] * count
        hi = [5.0] + [np.inf, np.inf, 1.5, 3.0] * count
        r = least_squares(residual, p0, bounds=(lo, hi), loss="soft_l1", f_scale=0.1, max_nfev=150)
        if best is None or r.cost < best.cost:
            best = r
    lights = sorted(lights_of(best.x), key=lambda light: -light[0])
    key = lights[0][0]
    return best.x[0] / key, [(c / key, d, s) for c, d, s in lights], float(best.cost)
