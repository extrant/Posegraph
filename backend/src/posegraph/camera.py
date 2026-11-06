"""照片镜头 → GPose 相机。

1. photo_camera_in_world：SAM 3D Body 给出拍摄相机相对人物的位置（cam_t）与焦距，
   经站姿重力校正、两套骨架的髋部对齐与体型比例缩放，再经角色的模型世界变换，得到 FFXIV 世界中的目标机位
2. solve_gpose_camera：按游戏内校准（scripts/camera_calibrate.py，2026-10-27）得出的换算直接算出参数，再小幅修正：
   - 相机位置 = 轴心 + Distance × 方向(水平角 Angle.x，俯仰 -Angle.y)，轴心固定（误差 1～2mm；|Angle.y| 超过约 1.4 时不准）
   - Pan、Roll 只转视线不动位置：视线的世界偏航 = 基准偏航 - Pan.x，滚转 = -Roll（都精确）；
     俯仰 ≈ 基准俯仰 + Pan.y（略不对称，±0.3 时差约 1°，修正一步）。基准视线（Pan=Roll=0）并不正对轴心，读一次即可
   - 垂直视角 ≈ 常数 + Zoom（弧度），下限约 12.6°
   - 大幅移动时游戏会平滑过渡约 0.6 秒：每次设置后等到相机不再移动再读（原来固定等 0.15 秒会读到过渡中的值，
     导致迭代很多次、画面一顿一顿）
   通常 3～4 次设置就到位（原来的有限差分迭代最多约 150 次）。
3. 设置时带 snap：插件同时写游戏平滑用的当前距离 / 视角，没有过渡，读数不会是半路的值。
   解完再按实测的位置、视线、上方向、视角误差闭环修正（最多 REFINE_ROUNDS 轮），到容差内为止——
   以前偶尔读到过渡中途的值或轴心估歪，差一点，要用户多点几次复刻镜头才对上。
"""

from __future__ import annotations

import time
from dataclasses import dataclass

import numpy as np
from scipy.spatial.transform import Rotation as R

from .bridge import BridgeClient

_FLIP = np.array([1.0, -1.0, -1.0])  # 相机系 ↔ MHR 模型系


@dataclass
class CameraTarget:
    position: np.ndarray  # 世界坐标
    forward: np.ndarray   # 视线方向（单位向量）
    up: np.ndarray        # 上方向（单位向量）
    fov_y_deg: float
    aspect: float = 16 / 9   # 照片宽高比


def game_fov_y(target: CameraTarget, game_aspect: float) -> float:
    """游戏该用的垂直视角（弧度）：照片整张放进游戏画面。照片比游戏画面窄（竖构图、4:3 等）时上下对齐，
    左右多出来的是构图框外；照片更宽（21:9 等）时改成左右对齐、上下留边，否则照片两侧会被裁掉、人显得太大。"""
    half = np.tan(np.radians(target.fov_y_deg) / 2)
    return float(2 * np.arctan(half * max(1.0, target.aspect / game_aspect)))


@dataclass
class CameraState:
    position: np.ndarray
    forward: np.ndarray
    up: np.ndarray
    fov_y_deg: float
    raw: dict
    frame: int | None = None   # 游戏帧号（插件给的；旧版插件没有）
    aspect: float = 16 / 9     # 游戏画面宽高比


def photo_camera_in_world(z, rt, model_position: np.ndarray, model_rotation: np.ndarray,
                          model_scale: np.ndarray, mhr_names: list[str], root_rotation_deg=None) -> CameraTarget:
    """z：image_to_pose 生成的 npz；rt：retarget 结果（需含 all_positions / upright_rotation）；
    model_*：角色模型的世界变换（Brio 姿势 JSON 的 ModelAbsoluteValues，旋转为 xyzw）；
    root_rotation_deg：调整页的整体旋转（度，模型系 xyz，同 RetargetParams.root_rotation）。给了的话镜头跟着人绕髋部一起转——
    整体旋转是用来把动作大致摆正的，镜头跟着转，画面里人和照片的相对角度不变（整体位移本来就跟着髋部走）。"""
    up_rot = R.from_quat(rt.upright_rotation) if rt.upright_rotation is not None else R.identity()
    manual = R.from_euler("xyz", root_rotation_deg, degrees=True) if root_rotation_deg is not None and any(root_rotation_deg) \
        else R.identity()
    mc = up_rot.apply(z["joint_coords"] * _FLIP)
    J = lambda n: mc[mhr_names.index(n)]
    cam_m = up_rot.apply(-z["cam_t"] * _FLIP)
    P = rt.all_positions
    mhr_hip, ff_hip = (J("l_upleg") + J("r_upleg")) / 2, (P["j_asi_a_l"] + P["j_asi_a_r"]) / 2
    # 体型比例：两套骨架的髋→颈长度之比，让机位按 FFXIV 角色的大小缩放、构图保持一致
    scale = np.linalg.norm(P["j_kubi"] - ff_hip) / np.linalg.norm(J("c_neck") - mhr_hip)
    cam_ff = ff_hip + scale * manual.apply(cam_m - mhr_hip)

    q = R.from_quat(model_rotation)
    w, h = (int(v) for v in z["image_size"])
    return CameraTarget(
        position=model_position + q.apply(model_scale * cam_ff),
        forward=q.apply(manual.apply(up_rot.apply([0.0, 0.0, -1.0]))),
        up=q.apply(manual.apply(up_rot.apply([0.0, 1.0, 0.0]))),
        fov_y_deg=float(np.degrees(2 * np.arctan(h / 2 / float(z["focal_length"])))),
        aspect=w / h,
    )


def rotate_target(target: CameraTarget, rotate: int) -> CameraTarget:
    """顺旋 / 逆旋：镜头绕视线转 90°，照片的宽对着游戏画面的高（拍竖图时游戏照常横屏，截图再转回来）。
    rotate = 1：照片的上方朝游戏画面左边（画面顺时针转 90° 变正）；-1：朝右边（逆时针转正）；0 不转。
    游戏相机的上方向 = ±照片的右方向，垂直视角换成照片的水平视角，宽高比取倒数。"""
    if rotate == 0:
        return target
    right = np.cross(target.forward, target.up)   # 照片的右方向（相机看 forward，上为 up）
    right /= np.linalg.norm(right)
    half_x = np.tan(np.radians(target.fov_y_deg) / 2) * target.aspect
    return CameraTarget(position=target.position, forward=target.forward, up=right * (1 if rotate > 0 else -1),
                        fov_y_deg=float(np.degrees(2 * np.arctan(half_x))), aspect=1 / target.aspect)


def read_state(client: BridgeClient) -> CameraState:
    r = client.get_camera()
    c = r["camera"]
    V = np.array(c["view"])  # 行向量约定：3×3 的列为相机轴（右、上、后）在世界中的方向
    P = c["projection"]
    fov = float(np.degrees(2 * np.arctan(1 / P[1][1])))
    return CameraState(np.array(c["position"]), -V[:3, 2], V[:3, 1], fov, c, r.get("frame"), float(P[1][1] / P[0][0]))


def _angle_between(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.arccos(np.clip(a @ b / np.linalg.norm(a) / np.linalg.norm(b), -1, 1)))


MAX_PITCH_PARAM = 1.4      # |Angle.y| 再大时轴心会偏（相机接近正上 / 正下）
MIN_FOV_RAD = 0.22         # Zoom 再小视角不再变小（约 12.6°）


def _direction(yaw: float, elevation: float) -> np.ndarray:
    return np.array([np.sin(yaw) * np.cos(elevation), np.sin(elevation), np.cos(yaw) * np.cos(elevation)])


def _view_angles(forward: np.ndarray, up: np.ndarray) -> np.ndarray:
    """相机朝向的世界角（偏航、俯仰、滚转），与 GPose 的 Pan / Roll 同一种分解（相机看 -z，先偏航再俯仰再滚转）。"""
    back = -forward / np.linalg.norm(forward)
    right = np.cross(up, back)
    right /= np.linalg.norm(right)
    return R.from_matrix(np.column_stack([right, np.cross(back, right), back])).as_euler("YXZ")


def _wrap(a: float) -> float:
    return float((a + np.pi) % (2 * np.pi) - np.pi)


SETTLE_FRAMES = 3   # 相机隔这么多帧都没动才算停稳
REFINE_ROUNDS = 4   # 解完后闭环修正最多几轮
POSITION_TOL = 0.01                 # 米
FORWARD_TOL = np.radians(0.15)      # 视线
UP_TOL = np.radians(0.3)            # 上方向（滚转）
FOV_TOL = np.radians(0.2)


def _settled(client: BridgeClient, timeout: float = 2.5, step: float = 0.03) -> CameraState:
    """设置后等相机停下来（游戏对大幅移动会平滑过渡约 0.6 秒）：隔 SETTLE_FRAMES 帧读回，位置差 < 0.5mm、视线差 < 0.02°。
    按帧号比，不按时间：游戏帧率低时（开着很多窗口、3D 预览），间隔 50ms 的两次读数可能是同一帧，会在过渡中途误判停稳。
    旧版插件不带帧号时退回按时间（每次间隔 step）。"""
    end = time.monotonic() + timeout
    time.sleep(step)
    last = read_state(client)
    while time.monotonic() < end:
        time.sleep(step)
        s = read_state(client)
        if s.frame is not None and last.frame is not None and s.frame - last.frame < SETTLE_FRAMES:
            continue
        if np.linalg.norm(s.position - last.position) < 5e-4 and _angle_between(s.forward, last.forward) < np.radians(0.02):
            return s
        last = s
    return last


def solve_gpose_camera(client: BridgeClient, target: CameraTarget, log=print) -> dict:
    state = read_state(client)
    raw = state.raw
    p = {"ax": raw["angle"][0], "ay": raw["angle"][1], "dist": raw["distance"],
         "px": 0.0, "py": 0.0, "roll": 0.0, "zoom": raw["zoom"]}
    moves = 0

    def apply() -> CameraState:
        nonlocal moves
        moves += 1
        client.set_camera(angle=[p["ax"], p["ay"]], distance=p["dist"], pan=[p["px"], p["py"]],
                          roll=p["roll"], zoom=p["zoom"], delimit=True, snap=True)
        return _settled(client)

    def pivot_of(s: CameraState) -> np.ndarray:
        r = s.raw
        return s.position - r["distance"] * _direction(r["angle"][0], -r["angle"][1])

    def place(pivot: np.ndarray) -> None:
        d = target.position - pivot
        p["dist"] = float(np.linalg.norm(d))
        p["ax"] = float(np.arctan2(d[0], d[2]))
        p["ay"] = float(np.clip(-np.arcsin(np.clip(d[1] / p["dist"], -1, 1)), -MAX_PITCH_PARAM, MAX_PITCH_PARAM))

    fov_offset = np.radians(state.fov_y_deg) - raw["zoom"]
    goal = _view_angles(target.forward, target.up)
    target_fov = max(game_fov_y(target, state.aspect), MIN_FOV_RAD)

    # 1. 位置与视角：按轴心直接算（当前相机接近正上 / 正下时轴心不准，到位后再算一次）
    place(pivot_of(state))
    p.update(px=0.0, py=0.0, roll=0.0, zoom=float(target_fov - fov_offset))
    s = apply()
    if np.linalg.norm(s.position - target.position) > 0.02:
        place(pivot_of(s))
        s = apply()

    # 2. 视线：基准视线（Pan=Roll=0）读到了，Pan.x、Roll 直接算；Pan.y 先按 1:1，再按实际俯仰修正
    base = _view_angles(s.forward, s.up)
    p["px"] = _wrap(base[0] - goal[0])
    p["roll"] = float(-goal[2])
    p["py"] = float(goal[1] - base[1])
    s = apply()
    for _ in range(2):
        now = _view_angles(s.forward, s.up)
        pitch_error = goal[1] - now[1]
        fov_error = target_fov - np.radians(s.fov_y_deg)
        if abs(pitch_error) < np.radians(0.1) and abs(fov_error) < np.radians(0.2):
            break
        p["py"] += float(pitch_error)
        if abs(fov_error) >= np.radians(0.2) and target_fov > MIN_FOV_RAD:
            p["zoom"] += float(fov_error)
        s = apply()

    # 3. 闭环修正：按这一刻实际的误差再拧（相当于替用户多点几次）。世界偏航 = 基准 − Pan.x、滚转 = −Roll，
    #    所以 Pan.x、Roll 按误差直接加；俯仰 Pan.y 按误差加；位置按当前轴心重新放；视角按误差改 Zoom。
    refined = 0
    for _ in range(REFINE_ROUNDS):
        s = read_state(client)
        position_error = float(np.linalg.norm(s.position - target.position))
        fov_error = target_fov - np.radians(s.fov_y_deg)
        fov_ok = abs(fov_error) < FOV_TOL or target_fov <= MIN_FOV_RAD
        if (position_error < POSITION_TOL and _angle_between(s.forward, target.forward) < FORWARD_TOL
                and _angle_between(s.up, target.up) < UP_TOL and fov_ok):
            break
        if position_error >= POSITION_TOL:
            place(pivot_of(s))
        now = _view_angles(s.forward, s.up)
        p["px"] = _wrap(p["px"] + _wrap(now[0] - goal[0]))
        p["py"] = float(p["py"] + goal[1] - now[1])
        p["roll"] = _wrap(p["roll"] + _wrap(now[2] - goal[2]))
        if not fov_ok:
            p["zoom"] += float(fov_error)
        apply()
        refined += 1

    final = read_state(client)
    result = {
        "params": p,
        "moves": moves,
        "refined": refined,
        "position_error_cm": float(np.linalg.norm(final.position - target.position) * 100),
        "forward_error_deg": float(np.degrees(_angle_between(final.forward, target.forward))),
        "up_error_deg": float(np.degrees(_angle_between(final.up, target.up))),
        "fov_deg": final.fov_y_deg,
        "target_fov_deg": float(np.degrees(target_fov)),
    }
    log(f"{moves} 次设置（闭环修正 {refined} 轮）：位置误差 {result['position_error_cm']:.1f}cm，视线误差 {result['forward_error_deg']:.2f}°，"
        f"上方向误差 {result['up_error_deg']:.2f}°，视角 {final.fov_y_deg:.1f}°/{np.degrees(target_fov):.1f}°")
    return result


_FRAMING_PAIRS = [("j_kao", "c_head"), ("j_ude_a_l", "l_uparm"), ("j_ude_a_r", "r_uparm"), ("j_te_l", "l_wrist"),
                  ("j_te_r", "r_wrist"), ("j_asi_a_l", "l_upleg"), ("j_asi_a_r", "r_upleg"), ("j_asi_c_l", "l_lowleg"),
                  ("j_asi_c_r", "r_lowleg"), ("j_asi_d_l", "l_foot"), ("j_asi_d_r", "r_foot")]


def framing_error(client: BridgeClient, z, pose, mhr_names: list[str], actor: int | None = None, rotate: int = 0) -> float:
    """构图检查：游戏相机下角色关节的屏幕位置 vs 照片中对应关节，返回平均偏差（占画面高度的比例）。
    只统计照片画面内的关节（半身照里画面外的腿是模型猜的）。
    坐标以游戏画面半高为单位、原点在中心；照片比游戏画面宽时照片按宽度放进画面（见 game_fov_y），换算时一并缩小。
    pose 为游戏当前姿势（BrioPose）；rotate 同 rotate_target（顺旋 / 逆旋时照片转 90° 放进画面）。"""
    cam = client.get_camera()["camera"]
    V, Pm = np.array(cam["view"]), np.array(cam["projection"])
    aspect = Pm[1][1] / Pm[0][0]
    vec = lambda s: np.array([float(x) for x in s.split(",")])
    m = pose.raw["ModelAbsoluteValues"]
    mp, mq, ms = vec(m["Position"]), R.from_quat(vec(m["Rotation"])), vec(m["Scale"])
    W, H = (int(v) for v in z["image_size"])
    f = float(z["focal_length"])
    long = W if rotate else H          # 照片在游戏画面里竖着的那条边
    fit = min(1.0, aspect / ((H / W) if rotate else (W / H)))   # 照片竖边的一半占游戏画面半高的比例
    jc = z["joint_coords"] + z["cam_t"]
    diffs = []
    for ff, mh in _FRAMING_PAIRS:
        clip = np.append(mp + mq.apply(ms * pose.get(ff).position), 1) @ V @ Pm
        game = np.array([clip[0] / clip[3] * aspect, clip[1] / clip[3]])
        p = jc[mhr_names.index(mh)]
        u, v = f * p[0] / p[2], -f * p[1] / p[2]   # 照片平面坐标（右、上，像素）
        if abs(u) > W / 2 or abs(v) > H / 2:
            continue
        # 顺旋 / 逆旋：照片的上方朝画面左边（rotate=1）时画面坐标 = (-v, u)，朝右边时 = (v, -u)
        photo = np.array([u, v] if rotate == 0 else [-v, u] if rotate > 0 else [v, -u]) / (long / 2)
        diffs.append(np.linalg.norm(game - photo * fit))
    return float(np.mean(diffs) / 2)
