"""SAM 3D Body 快速模式：把 Fast SAM 3D Body 里在我们场景下有用的提速手段做成可开关的补丁，上游源码不改。

来源与许可：
- 下面的 _decoder_forward / _run_inference 改写自 Meta SAM 3D Body（third_party/sam-3d-body，SAM License）
  的 PromptableDecoder.forward 与 SAM3DBody.run_inference；
- 提速思路和参数取自 Fast SAM 3D Body（github.com/yangtiming/Fast-SAM-3D-Body，MIT，arXiv 2603.15603）。

在 RTX 4060 笔记本上实测，时间主要花在解码器每一层的「中间预测」（每次都要跑一遍 MHR 前向），骨干只占小头，
所以只移植了下面几项（论文里的 TensorRT 骨干、torch.compile、YOLO-Pose 定手框都没用，原因见 FastOptions）：
- 中间预测只留前几层（身体 {0,1,2}、手 {0,1}），其余层的关键点 token 更新沿用最近一次的预测；
- 左右手合成一批，骨干 + 手部解码器只走一次；
- 关键点提示（用手部结果的手腕把身体手腕拉过去）那次解码只在最后一层出结果，也可以整个跳过；
- MHR 不算修正形变（只影响网格顶点，不影响骨架）。
同时去掉了上游写死的 .cuda()，CPU 模式也能用这些补丁。
"""

from __future__ import annotations

import types
from dataclasses import dataclass
from typing import Any, Dict, Optional


@dataclass(frozen=True)
class FastOptions:
    """body_interm_layers / hand_interm_layers：做中间预测的解码器层（None = 每层都做，同上游；空集 = 只在最后出结果）；
    keypoint_prompt：是否跑关键点提示那次解码（False 时手腕只按手部结果的旋转合并，不再把身体手腕位置拉过去）；
    keypoint_prompt_layers：那次解码做中间预测的层（默认只出最终结果）；
    merge_hands：左右手合成一批；correctives：MHR 修正形变；
    sdpa_backbone：骨干的注意力改用 PyTorch 自带的 scaled_dot_product_attention（同一套权重、数学上等价；
    上游的 FlashAttention 选项要 flash_attn 库，Windows 装不上）；decoder_bf16：解码器的 Transformer 层用 bf16 算（只在显卡上）；
    backbone_scale：骨干输入缩放（0.75 = 512×384 → 384×288），见 _scaled_backbone。只留作实验（环境变量
    POSEGRAPH_BACKBONE_SCALE），不进插件设置：2026-10-28 实测 CPU 单人 7.8→4.7 秒，但关节平均差 53mm、
    每个关节旋转中位差约 8°（显卡上只快约 12%）；0.875 也有 34mm。ViT-H 的位置编码按固定分辨率训练，缩小输入就明显变差。
    同一天试过骨干 int8 动态量化（按层 / 按通道 / 只量化 MLP）：CPU 快约 1.6 倍，但误差都在 30mm 以上，也没采用。

    默认值是在 RTX 4060 笔记本、9 张测试图上和上游逐项对比后选的（2026-10-28）：只开「合并双手」和「关修正形变」，
    单人 1.33→0.77 秒（MHR 放 CPU 时）/ 0.79→0.57 秒（MHR 在显卡），和上游的差别：关节平均 2~6mm、2D 关键点 4~8px，
    和上游自己换个注意力实现的数值差同一量级。其余几项都试过、默认关掉：
    - 裁剪中间预测：只再省约 20ms，关节差翻到 7~11mm（每层的关键点 token 更新要用中间预测，跳过后后面几层用的是旧结果）；
    - 关键点提示那次解码只出最终结果：2D 平均差 100px 以上，姿势被带偏，不能用；跳过整次解码：省约 0.1 秒，关节差约 25mm；
    - SDPA 骨干、解码器 bf16：4060 上没有变快，反而多出约 5mm 的数值差。

    没移植的：YOLO-Pose 定手框后身体和双手一次前向（检测器 AGPL，而且手被遮挡时手腕框会放错）；
    输入 384（权重按 512 训练，换尺寸要重新验证）；TensorRT / torch.compile（骨干本来就只占约 20%，
    Windows 上还要额外装 TensorRT 或 Triton，换卡要重建引擎）。"""

    body_interm_layers: Optional[frozenset] = None
    hand_interm_layers: Optional[frozenset] = None
    keypoint_prompt: bool = True
    keypoint_prompt_layers: Optional[frozenset] = None
    merge_hands: bool = True
    correctives: bool = False
    sdpa_backbone: bool = False
    decoder_bf16: bool = False
    backbone_scale: float = 1.0


ORIGINAL = FastOptions(body_interm_layers=None, hand_interm_layers=None, keypoint_prompt=True, keypoint_prompt_layers=None,
                       merge_hands=False, correctives=True)


def apply(model, options: FastOptions) -> None:
    """给已加载的 SAM3DBody 实例装上补丁（只改这个实例，不动类，共享模型 / 其他实例不受影响）。"""
    model._pg_fast = options
    model.decoder._pg_layers = options.body_interm_layers
    model.decoder_hand._pg_layers = options.hand_interm_layers
    model.decoder.forward = types.MethodType(_decoder_forward, model.decoder)
    model.decoder_hand.forward = types.MethodType(_decoder_forward, model.decoder_hand)
    model.run_inference = types.MethodType(_run_inference, model)
    from sam_3d_body.models.backbones import vit
    for m in model.backbone.modules():
        if type(m) is vit.Attention:
            if options.sdpa_backbone:
                m.forward = types.MethodType(_sdpa_attention, m)
            elif "forward" in m.__dict__:
                del m.forward
    model.decoder._pg_bf16 = model.decoder_hand._pg_bf16 = options.decoder_bf16
    backbone = model.backbone
    backbone._pg_scale = options.backbone_scale
    backbone._pg_pos = {}
    if options.backbone_scale != 1.0:
        backbone.forward = types.MethodType(_scaled_backbone, backbone)
    elif "forward" in backbone.__dict__:
        del backbone.forward
    # 可以反复切换：记住原来的 MHR（可能已被换成「放在 CPU」的包装），每次从它重新包
    if not hasattr(model, "_pg_mhr"):
        model._pg_mhr = (model.head_pose.mhr, model.head_pose_hand.mhr)
    body, hand = model._pg_mhr
    if not options.correctives:
        wrapped = _no_correctives(body)
        body, hand = wrapped, wrapped if hand is body else _no_correctives(hand)
    model.head_pose.mhr, model.head_pose_hand.mhr = body, hand


_no_correctives_cls = None


def _no_correctives(mhr):
    """包一层 MHR：调用时 apply_correctives=False（姿势相关的修正形变只改网格顶点，骨架不变；GPU 上 MHR 约快 10 倍）。"""
    global _no_correctives_cls
    import torch

    if _no_correctives_cls is None:
        class NoCorrectives(torch.nn.Module):
            def __init__(self, inner):
                super().__init__()
                # 不注册为子模块：外层 .to(device) 不会把它搬走（放在 CPU 上的 MHR 保持在 CPU）
                object.__setattr__(self, "inner", inner)

            def forward(self, shape, params, expr):
                return self.inner(shape, params, expr, False)

            def parameters(self, recurse: bool = True):
                return self.inner.parameters(recurse)

        _no_correctives_cls = NoCorrectives
    return mhr if isinstance(mhr, _no_correctives_cls) else _no_correctives_cls(mhr)


def _sdpa_attention(self, x):
    """同上游 vit.Attention.forward（q·kᵀ 缩放后 softmax 再乘 v），改用 scaled_dot_product_attention：不存整张注意力矩阵，更快更省显存。"""
    import torch.nn.functional as F

    B, N, _ = x.shape
    qkv = self.qkv(x).reshape(B, N, 3, self.num_heads, -1).permute(2, 0, 3, 1, 4)
    x = F.scaled_dot_product_attention(qkv[0], qkv[1], qkv[2], scale=self.scale)
    return self.proj_drop(self.proj(x.transpose(1, 2).reshape(B, N, -1)))


def strip_correctives(mhr) -> None:
    """删掉 MHR 里修正形变的权重（634MB，占整个 MHR 的 96%），剩约 29MB，可以直接放进显卡。
    之后只能用 apply_correctives=False 调用（apply 会包好）；误开修正形变会直接报错而不是给出错结果。"""
    import torch

    linear = mhr.pose_correctives_model.pose_dirs_predictor._modules["2"]
    linear.weight = torch.nn.Parameter(torch.empty(0, linear.weight.shape[1]), requires_grad=False)


def _scaled_backbone(self, x, extra_embed=None):
    """低配模式：骨干在缩小的图上算，算完把特征图放大回原尺寸，下游（解码器、遮罩 / 射线条件的裁剪）完全不变。

    不直接改 MODEL.IMAGE_SIZE：ViT-H 的位置编码按 512×384 训练、固定大小，上游还有几处按这个尺寸写死的裁剪
    （图像、射线条件、遮罩提示各裁 64 像素 / 4 格）。这里只把位置编码插值到缩小后的网格。
    Fast SAM 的 384 输入用的是 DINOv3 骨干（尺寸自适应），和这里不是一回事，精度要单独验证。"""
    import torch
    import torch.nn.functional as F

    h, w = x.shape[-2:]
    hs, ws = round(h * self._pg_scale / 16) * 16, round(w * self._pg_scale / 16) * 16
    small = F.interpolate(x.float(), size=(hs, ws), mode="bilinear", align_corners=False, antialias=True).to(x.dtype)
    tokens, (hp, wp) = self.patch_embed(small)
    if self.pos_embed is not None:
        key = (hp, wp, tokens.dtype, tokens.device)
        if key not in self._pg_pos:
            # 位置编码第 0 个是常数项（上游写法：pos_embed[:, 1:] + pos_embed[:, :1]），其余按原网格双三次插值
            n = self.pos_embed.shape[1] - 1
            gh, gw = self.patch_embed.patch_shape if hasattr(self.patch_embed, "patch_shape") else (h // 16, w // 16)
            assert gh * gw == n, (gh, gw, n)
            grid = self.pos_embed[:, 1:].reshape(1, gh, gw, -1).permute(0, 3, 1, 2).float()
            grid = F.interpolate(grid, size=(hp, wp), mode="bicubic", align_corners=False)
            self._pg_pos[key] = (grid.permute(0, 2, 3, 1).reshape(1, hp * wp, -1) + self.pos_embed[:, :1].float()).to(tokens)
        tokens = tokens + self._pg_pos[key]
    if extra_embed is not None:
        raise NotImplementedError("低配模式不支持骨干的 extra_embed（上游此处总是传 None）")
    for blk in self.blocks:
        tokens = blk(tokens)
    tokens = self.last_norm(tokens)
    out = tokens.permute(0, 2, 1).reshape(tokens.shape[0], -1, hp, wp)
    # 放大回原网格（512×384 → 32×24）
    full = (h + 4 - 16) // 16 + 1, (w + 4 - 16) // 16 + 1
    return F.interpolate(out.float(), size=full, mode="bilinear", align_corners=False).to(out.dtype).contiguous()


# ---------------------------------------------------------------- 解码器

def _decoder_forward(
    self,
    token_embedding,
    image_embedding,
    token_augment=None,
    image_augment=None,
    token_mask=None,
    channel_first: bool = True,
    token_to_pose_output_fn=None,
    keypoint_token_update_fn=None,
    hand_embeddings=None,
    hand_augment=None,
):
    """同上游 PromptableDecoder.forward，区别：只在 self._pg_layers 里的层做中间预测（None 为每层都做）；
    跳过的层照样做关键点 token 更新，用最近一次的预测（Fast SAM 的做法）。"""
    import torch

    layers = getattr(self, "_pg_layers", None)
    bf16 = getattr(self, "_pg_bf16", False) and token_embedding.is_cuda
    if channel_first:
        image_embedding = image_embedding.flatten(2).permute(0, 2, 1)
        if image_augment is not None:
            image_augment = image_augment.flatten(2).permute(0, 2, 1)
        if hand_embeddings is not None:
            hand_embeddings = hand_embeddings.flatten(2).permute(0, 2, 1)
            hand_augment = hand_augment.flatten(2).permute(0, 2, 1)
            if len(hand_augment) == 1:
                assert len(hand_augment.shape) == 3
                hand_augment = hand_augment.repeat(len(hand_embeddings), 1, 1)

    if self.do_interm_preds:
        assert token_to_pose_output_fn is not None
    all_pose_outputs = []
    last = len(self.layers) - 1
    for layer_idx, layer in enumerate(self.layers):
        with torch.autocast("cuda", dtype=torch.bfloat16, enabled=bf16):
            if hand_embeddings is None:
                token_embedding, image_embedding = layer(token_embedding, image_embedding, token_augment, image_augment, token_mask)
            else:
                token_embedding, image_embedding = layer(
                    token_embedding,
                    torch.cat([image_embedding, hand_embeddings], dim=1),
                    token_augment,
                    torch.cat([image_augment, hand_augment], dim=1),
                    token_mask,
                )
                image_embedding = image_embedding[:, : image_augment.shape[1]]
        if bf16:   # 中间预测（头部、MHR）照旧用 fp32
            token_embedding, image_embedding = token_embedding.float(), image_embedding.float()

        if self.do_interm_preds and layer_idx < last:
            if layers is None or layer_idx in layers:
                all_pose_outputs.append(token_to_pose_output_fn(
                    self.norm_final(token_embedding),
                    prev_pose_output=all_pose_outputs[-1] if all_pose_outputs else None,
                    layer_idx=layer_idx,
                ))
            if self.keypoint_token_update and all_pose_outputs:
                assert keypoint_token_update_fn is not None
                token_embedding, token_augment, _, _ = keypoint_token_update_fn(
                    token_embedding, token_augment, all_pose_outputs[-1], layer_idx
                )

    out = self.norm_final(token_embedding)
    if self.do_interm_preds:
        all_pose_outputs.append(token_to_pose_output_fn(
            out, prev_pose_output=all_pose_outputs[-1] if all_pose_outputs else None, layer_idx=last,
        ))
        return out, all_pose_outputs
    return out


# ---------------------------------------------------------------- 整体推理

_BATCH_KEYS = ("img", "img_size", "ori_img_size", "bbox_center", "bbox_scale", "bbox", "affine_trans",
               "mask", "mask_score", "person_valid")


def _cat_people(a: Dict, b: Dict) -> Dict:
    """两个 [1, N, ...] 的批次按人数维拼成 [1, 2N, ...]（左手在前、右手在后）。"""
    import torch

    out = {k: torch.cat([a[k], b[k]], dim=1) for k in _BATCH_KEYS if k in a and k in b}
    if "cam_int" in a:
        out["cam_int"] = a["cam_int"]
    if "img_ori" in a and "img_ori" in b:
        out["img_ori"] = a["img_ori"] + b["img_ori"]
    return out


def _split_hands(output: Dict, n: int) -> tuple[Dict, Dict]:
    import torch

    left, right = {"mhr_hand": {}}, {"mhr_hand": {}}
    for k, v in (output.get("mhr_hand") or {}).items():
        left["mhr_hand"][k] = v[:n] if isinstance(v, torch.Tensor) else v
        right["mhr_hand"][k] = v[n:] if isinstance(v, torch.Tensor) else v
    return left, right


def _run_inference(self, img, batch: Dict, inference_type: str = "full", transform_hand: Any = None,
                   thresh_wrist_angle=1.4):
    """同上游 SAM3DBody.run_inference（full 模式），区别：左右手合成一批；关键点提示可跳过 / 只出最终结果；
    设备跟着输入走（上游写死 .cuda()）。返回值和上游一致。"""
    import roma
    import torch
    import sam_3d_body.models.meta_arch.sam3d_body as meta

    opts: FastOptions = self._pg_fast
    if inference_type in ("body", "hand"):
        return self.forward_step(batch, decoder_type=inference_type)
    device = batch["img"].device
    height, width = img.shape[:2]
    cam_int = batch["cam_int"].clone()

    # 1. 身体
    pose_output = self.forward_step(batch, decoder_type="body")
    left_xyxy, right_xyxy = self._get_hand_box(pose_output, batch)
    ori_local_wrist_rotmat = roma.euler_to_rotmat(
        "XZY", pose_output["mhr"]["body_pose"][:, [41, 43, 42, 31, 33, 32]].unflatten(1, (2, 3)))

    # 2. 双手：左手翻转成右手来估（手部解码器只认右手）
    flipped_img = img[:, ::-1]
    tmp = left_xyxy.copy()
    left_xyxy[:, 0] = width - tmp[:, 2] - 1
    left_xyxy[:, 2] = width - tmp[:, 0] - 1
    batch_lhand = meta.recursive_to(meta.prepare_batch(flipped_img, transform_hand, left_xyxy, cam_int=cam_int.clone()), device)
    batch_rhand = meta.recursive_to(meta.prepare_batch(img, transform_hand, right_xyxy, cam_int=cam_int.clone()), device)
    if opts.merge_hands:
        hands = _cat_people(batch_lhand, batch_rhand)
        self._initialize_batch(hands)
        lhand_output, rhand_output = _split_hands(self.forward_step(hands, decoder_type="hand"),
                                                  batch_lhand["img"].shape[0] * batch_lhand["img"].shape[1])
        self._initialize_batch(batch)
    else:
        lhand_output = self.forward_step(batch_lhand, decoder_type="hand")
        rhand_output = self.forward_step(batch_rhand, decoder_type="hand")

    # 左手结果翻转回来
    scale_r_mean = self.head_pose.scale_mean[8].item()
    scale_l_mean = self.head_pose.scale_mean[9].item()
    scale_r_std = self.head_pose.scale_comps[8, 8].item()
    scale_l_std = self.head_pose.scale_comps[9, 9].item()
    lh = lhand_output["mhr_hand"]
    lh["scale"][:, 9] = ((scale_r_mean + scale_r_std * lh["scale"][:, 8]) - scale_l_mean) / scale_l_std
    lh["joint_global_rots"][:, 78] = lh["joint_global_rots"][:, 42].clone()
    lh["joint_global_rots"][:, 78, [1, 2], :] *= -1
    lh["hand"][:, :54] = lh["hand"][:, 54:]
    batch_lhand["bbox_center"][:, :, 0] = width - batch_lhand["bbox_center"][:, :, 0] - 1
    rh = rhand_output["mhr_hand"]

    # 3. 判断手部结果能不能用（四条标准同上游）
    lowarm_idx = torch.tensor([76, 40], device=device)
    twist_idx = torch.tensor([77, 41], device=device)
    wrist_zero = pose_output["mhr"]["joint_global_rots"][:, lowarm_idx] @ self.head_pose.joint_rotation[twist_idx]
    pred_wrist = torch.stack([lh["joint_global_rots"][:, 78], rh["joint_global_rots"][:, 42]], dim=1)
    fused_local = torch.einsum("kabc,kabd->kadc", pred_wrist, wrist_zero)
    angle_ok = meta.rotation_angle_difference(ori_local_wrist_rotmat, fused_local) < thresh_wrist_angle
    size_ok = torch.stack([(batch_lhand["bbox_scale"].flatten(0, 1) > 64).all(dim=1),
                           (batch_rhand["bbox_scale"].flatten(0, 1) > 64).all(dim=1)], dim=1)
    inside_ok = torch.stack([lh["pred_keypoints_2d_cropped"].abs().amax(dim=(1, 2)) < 0.5,
                             rh["pred_keypoints_2d_cropped"].abs().amax(dim=(1, 2)) < 0.5], dim=1)
    r_wrist, l_wrist = 41, 62
    right_full = rh["pred_keypoints_2d"][:, [r_wrist]].clone()
    left_full = lh["pred_keypoints_2d"][:, [r_wrist]].clone()
    left_full[:, :, 0] = width - left_full[:, :, 0] - 1
    body_kps = pose_output["mhr"]["pred_keypoints_2d"]
    right_dist = (right_full - body_kps[:, [r_wrist]]).flatten(0, 1).norm(dim=-1) / batch_lhand["bbox_scale"].flatten(0, 1)[:, 0]
    left_dist = (left_full - body_kps[:, [l_wrist]]).flatten(0, 1).norm(dim=-1) / batch_rhand["bbox_scale"].flatten(0, 1)[:, 0]
    wrist_ok = torch.stack([left_dist < 0.25, right_dist < 0.25], dim=1)
    hand_valid_mask = angle_ok & size_ok & inside_ok & wrist_ok

    # 关键点提示：手部结果的手腕 + 身体的肘作为提示，身体解码器再跑一次
    if opts.keypoint_prompt:
        batch_size, num_person = batch["img"].shape[:2]
        self.hand_batch_idx = []
        self.body_batch_idx = list(range(batch_size * num_person))
        r_elbow, l_elbow = 8, 7
        prompt = torch.cat([
            self._full_to_crop(batch, right_full), self._full_to_crop(batch, left_full),
            self._full_to_crop(batch, body_kps[:, [r_elbow]].clone()), self._full_to_crop(batch, body_kps[:, [l_elbow]].clone()),
        ], dim=1)
        prompt = torch.cat([prompt, prompt[..., [-1]]], dim=-1)
        prompt[:, 0, -1], prompt[:, 1, -1], prompt[:, 2, -1], prompt[:, 3, -1] = r_wrist, l_wrist, r_elbow, l_elbow
        if prompt.shape[0] > 1:
            invalid = ((prompt[..., 0] < -0.5) | (prompt[..., 0] > 0.5) | (prompt[..., 1] < -0.5) | (prompt[..., 1] > 0.5)
                       | (~hand_valid_mask[..., [1, 0, 1, 0]])).unsqueeze(-1)
            dummy = torch.zeros((1, 1, 3)).to(prompt)
            dummy[:, :, -1] = -2
            prompt[:, :, :2] = torch.clamp(prompt[:, :, :2] + 0.5, min=0.0, max=1.0)
            prompt = torch.where(invalid, dummy, prompt)
        else:
            valid = (torch.all((prompt[:, :, :2] > -0.5) & (prompt[:, :, :2] < 0.5), dim=2)
                     & hand_valid_mask[..., [1, 0, 1, 0]]).squeeze()
            prompt = prompt[:, valid]
            prompt[:, :, :2] = torch.clamp(prompt[:, :, :2] + 0.5, min=0.0, max=1.0)
        if prompt.numel() != 0:
            saved = self.decoder._pg_layers
            self.decoder._pg_layers = opts.keypoint_prompt_layers
            try:
                pose_output, _ = self.run_keypoint_prompt(batch, pose_output, prompt)
            finally:
                self.decoder._pg_layers = saved

    # 4. 把手部结果合进来（同上游）
    updated_hand_pose = torch.cat([lh["hand"][:, :54], rh["hand"][:, 54:]], dim=1)
    updated_scale = pose_output["mhr"]["scale"].clone()
    updated_scale[:, 9] = lh["scale"][:, 9]
    updated_scale[:, 8] = rh["scale"][:, 8]
    updated_scale[:, 18:] = (lh["scale"][:, 18:] + rh["scale"][:, 18:]) / 2
    updated_shape = pose_output["mhr"]["shape"].clone()
    updated_shape[:, 40:] = (lh["shape"][:, 40:] + rh["shape"][:, 40:]) / 2

    joint_rotations = self.head_pose.mhr_forward(
        global_trans=pose_output["mhr"]["global_rot"] * 0, global_rot=pose_output["mhr"]["global_rot"],
        body_pose_params=pose_output["mhr"]["body_pose"], hand_pose_params=updated_hand_pose,
        scale_params=updated_scale, shape_params=updated_shape, expr_params=pose_output["mhr"]["face"],
        return_joint_rotations=True,
    )[1]
    wrist_zero = joint_rotations[:, lowarm_idx] @ self.head_pose.joint_rotation[twist_idx]
    fused_local = torch.einsum("kabc,kabd->kadc", pred_wrist, wrist_zero)
    wrist_xzy = meta.fix_wrist_euler(roma.rotmat_to_euler("XZY", fused_local))
    valid_angle = ((meta.rotation_angle_difference(ori_local_wrist_rotmat, fused_local) < thresh_wrist_angle)
                   & hand_valid_mask).unsqueeze(-1)
    mhr = pose_output["mhr"]
    body_pose = mhr["body_pose"][:, [41, 43, 42, 31, 33, 32]].unflatten(1, (2, 3))
    mhr["body_pose"][:, [41, 43, 42, 31, 33, 32]] = torch.where(valid_angle, wrist_xzy, body_pose).flatten(1, 2)
    mhr["hand"] = torch.where(valid_angle, updated_hand_pose.unflatten(1, (2, 54)), mhr["hand"].unflatten(1, (2, 54))).flatten(1, 2)
    hand_scale = torch.stack([mhr["scale"][:, 9], mhr["scale"][:, 8]], dim=1)
    masked = torch.where(valid_angle.squeeze(-1), torch.stack([updated_scale[:, 9], updated_scale[:, 8]], dim=1), hand_scale)
    mhr["scale"][:, 9] = masked[:, 0]
    mhr["scale"][:, 8] = masked[:, 1]
    va = valid_angle.squeeze(-1)
    any_valid = va.sum(dim=1, keepdim=True) > 0
    denom = va.sum(dim=1, keepdim=True) + 1e-8
    mhr["scale"][:, 18:] = torch.where(any_valid, (lh["scale"][:, 18:] * va[:, [0]] + rh["scale"][:, 18:] * va[:, [1]]) / denom,
                                       mhr["scale"][:, 18:])
    mhr["shape"][:, 40:] = torch.where(any_valid, (lh["shape"][:, 40:] * va[:, [0]] + rh["shape"][:, 40:] * va[:, [1]]) / denom,
                                       mhr["shape"][:, 40:])

    # 5. 用最终参数重跑 MHR、投影回 2D（同上游）
    with torch.no_grad():
        verts, j3d, jcoords, mhr_model_params, _ = self.head_pose.mhr_forward(
            global_trans=mhr["global_rot"] * 0, global_rot=mhr["global_rot"], body_pose_params=mhr["body_pose"],
            hand_pose_params=mhr["hand"], scale_params=mhr["scale"], shape_params=mhr["shape"], expr_params=mhr["face"],
            return_keypoints=True, return_joint_coords=True, return_model_params=True, return_joint_rotations=True,
        )
        j3d = j3d[:, :70]
        verts[..., [1, 2]] *= -1
        j3d[..., [1, 2]] *= -1
        jcoords[..., [1, 2]] *= -1
        mhr["pred_keypoints_3d"] = j3d
        mhr["pred_vertices"] = verts
        mhr["pred_joint_coords"] = jcoords
        mhr["pred_pose_raw"][...] = 0
        mhr["mhr_model_params"] = mhr_model_params
    proj = mhr["pred_keypoints_3d"] + mhr["pred_cam_t"][:, None, :]
    proj[:, :, [0, 1]] *= mhr["focal_length"][:, None, None]
    proj[:, :, [0, 1]] = proj[:, :, [0, 1]] + torch.FloatTensor([width / 2, height / 2]).to(proj)[None, None, :] * proj[:, :, [2]]
    proj[:, :, :2] = proj[:, :, :2] / proj[:, :, [2]]
    mhr["pred_keypoints_2d"] = proj[:, :, :2]
    return pose_output, batch_lhand, batch_rhand, lhand_output, rhand_output
