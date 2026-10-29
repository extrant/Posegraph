"""返回给插件界面的文字的多语言（简体中文 / 英文）。

插件在每个请求的 Accept-Language 里带上界面语言（zh / en），中间件写入 _lang，
tr() 按它查表（以中文原文为键，查不到原样返回），再用关键字参数格式化。命令行脚本的输出不翻译。
"""

from __future__ import annotations

import os
from contextvars import ContextVar

_lang: ContextVar[str] = ContextVar("lang", default=os.environ.get("POSEGRAPH_LANG", "zh"))


def set_language(header: str | None) -> None:
    code = (header or "").split(",")[0].strip().lower()
    _lang.set("en" if code.startswith("en") else "zh" if code.startswith("zh") else _lang.get())


def tr(text: str, **kwargs) -> str:
    out = EN.get(text, text) if _lang.get() == "en" else text
    return out.format(**kwargs) if kwargs else out


EN = {
    # 面捕（face.py）
    "面捕组件没装（mediapipe 或 face_landmarker 模型）": "Face capture is not installed (mediapipe or the face_landmarker model)",
    "还没有游戏表情数据：在游戏里输入 /posegraph facedump 导出一次": "No game expression data yet: type /posegraph facedump in game once to export it",
    "姿势里没有头部骨骼": "The pose has no head bone",
    "左眼闭": "Left eye closed", "右眼闭": "Right eye closed", "左眼睁大": "Left eye wide", "右眼睁大": "Right eye wide",
    "左眼眯": "Left eye squint", "右眼眯": "Right eye squint", "左眉头高(cm)": "Left inner brow up (cm)", "右眉头高(cm)": "Right inner brow up (cm)",
    "左嘴角上(cm)": "Left mouth corner up (cm)", "右嘴角上(cm)": "Right mouth corner up (cm)",
    "左嘴角外(cm)": "Left mouth corner out (cm)", "右嘴角外(cm)": "Right mouth corner out (cm)",
    "上唇上提(cm)": "Upper lip up (cm)", "下唇下拉(cm)": "Lower lip down (cm)", "下巴张开(cm)": "Jaw open (cm)", "嘴偏向左(cm)": "Mouth shifted left (cm)",
    "头不在画面里": "The head is outside the image",
    "脸太小": "The face is too small",
    "脸上能看清的皮肤太少（脸太小或被挡住）": "Too little of the face's skin is visible (the face is too small or covered)",
    "人在照片里太小，看不清明暗": "the person is too small in the photo to see the shading",
    "；": "; ",
    "没找到脸（插画、遮挡或背对）": "No face found (illustration, covered, or facing away)",
    "找到的脸不是这个人的（可能是旁边的人）": "The face found belongs to someone else (probably the person next to them)",
    "侧脸太多": "The face is turned too far to the side",
    "置信度太低": "Confidence too low",
    # 合理性提示（tuning.LIMITS 与组合检查）
    "腰部转动过大": "Waist rotation too large",
    "胸背单节转动过大，躯干可能折角": "Chest/back segment rotation too large; the torso may kink",
    "颈部转动过大，可能出现细长/折角的脖子（插画常见）": "Neck rotation too large; the neck may look stretched or kinked (common with illustrations)",
    "头部转动过大（插画常见）": "Head rotation too large (common with illustrations)",
    "锁骨转动过大，斜方肌可能隆起、肩部出尖角": "Clavicle rotation too large; the trapezius may bulge and the shoulder spike",
    "上臂扭转过大，肩部辅助骨会变形": "Upper arm twist too large; shoulder helper bones will deform",
    "肘部弯曲超过游戏动画常见最大值（约 130°），肘窝会有褶皱": "Elbow bend beyond the usual in-game maximum (~130°); the inner elbow will crease",
    "肘部有侧向摆动（游戏里肘是纯铰链），蒙皮会扭曲": "Sideways elbow swing (in-game elbows are pure hinges); skinning will twist",
    "手腕转动过大": "Wrist rotation too large",
    "膝盖弯曲过大": "Knee bend too large",
    "膝盖有侧向摆动（游戏里膝是纯铰链）": "Sideways knee swing (in-game knees are pure hinges)",
    "颈和头同向侧倾叠加过大，脖子会显得歪拧（插画常见）": "Neck and head tilt the same way too much; the neck will look twisted (common with illustrations)",
    "颈和头扭转方向相反，脖子会显得拧": "Neck and head twist in opposite directions; the neck will look wrung",
    "{side}手末端补偿移动了 {shift:.0f}cm（手贴近头/躯干时为保持接触），不满意可调低补偿强度":
        "{side} hand moved {shift:.0f} cm by contact compensation (keeps contact with the head/torso); lower the strength if you dislike it",
    "左": "Left",
    "右": "Right",
    # 种族
    "人族中原 男": "Midlander Hyur (M)", "人族高地 男": "Highlander Hyur (M)", "猫魅 男": "Miqo'te (M)", "敖龙 男": "Au Ra (M)",
    "维埃拉 男": "Viera (M)", "精灵 男": "Elezen (M)", "鲁加 男": "Roegadyn (M)", "硌狮 男": "Hrothgar (M)", "拉拉菲尔 男": "Lalafell (M)",
    "人族中原 女": "Midlander Hyur (F)", "人族高地 女": "Highlander Hyur (F)", "维埃拉 女": "Viera (F)", "精灵 女": "Elezen (F)",
    "鲁加 女": "Roegadyn (F)", "硌狮 女": "Hrothgar (F)", "猫魅 女": "Miqo'te (F)", "敖龙 女": "Au Ra (F)", "拉拉菲尔 女": "Lalafell (F)",
    "中原系男": "Midlander-type (M)", "精灵男": "Elezen (M)", "鲁加系男": "Roegadyn-type (M)", "拉拉菲尔": "Lalafell",
    "中原系女": "Midlander-type (F)", "精灵系女": "Elezen-type (F)", "猫魅系女": "Miqo'te-type (F)",
    "游戏角色的骨长更像{group}，与所选{name}不符（刚换过种族？改体型 MOD？），身体比例会不对":
        "The actor's bone lengths look like {group}, not the selected {name} (race just changed? body mod?); proportions will be off",
    "没有 {code} 的骨架数据，按骨长推断为{group}": "No skeleton data for {code}; inferred {group} from bone lengths",
    "游戏骨长与{name}相差 {pct}%（Customize+ 或改体型 MOD？），按游戏实际骨长计算":
        "Game bone lengths differ from {name} by {pct}% (Customize+ or a body mod?); using the game lengths",
    "游戏骨长与{name}相差 {pct}%，多半是换种族前残留的 Brio 姿势，已改用{name}的标准骨长":
        "Game bone lengths differ from {name} by {pct}%, most likely a Brio pose left over from before a race change; using standard {name} lengths",
    "无法确定种族，按人族中原男计算": "Could not determine the race; using Midlander Hyur (M)",
    "（没有 {code} 的骨架数据）": " (no skeleton data for {code})",
    "未检测到 IVCS 骨骼：手指第三节的弯曲已合并到第二节":
        "IVCS skeleton not detected: the bend of each finger's third joint has been merged into the second",
    # 设备
    "手动指定": "set manually",
    "未检测到 NVIDIA 显卡": "no NVIDIA GPU detected",
    "可用显存 {free} MB，低于 {need} MB": "{free} MB VRAM free, below {need} MB",
    "可用显存 {free} MB": "{free} MB VRAM free",
    # 错误
    "device 只能是 auto / cuda / cpu": "device must be auto / cuda / cpu",
    "无法解码图片：{error}": "Cannot decode the image: {error}",
    "会话不存在（后端重启过？请重新识别）": "Session not found (backend restarted?); please recognize again",
    "识别缓存无法读取：{error}": "Cannot read the recognition cache: {error}",
    "请求体为空": "Empty request body",
    "模型加载失败：{error}": "Model failed to load: {error}",
    "未检测到人物": "No person detected",
    "找不到人物检测模型：{path}": "Person detection model not found: {path}",
    "后端缺少人物检测组件（onnxruntime），请更新一键包": "The backend is missing the person detection component (onnxruntime); update the backend package",
    "人物框格式不对，应为 x1,y1,x2,y2;…": "Invalid person boxes; expected x1,y1,x2,y2;…",
    "人物框太小或超出图片范围": "A person box is too small or outside the image",
    "人物遮罩要和人物框、遮罩尺寸一起给": "Person masks need person boxes and the mask size",
    "人物遮罩不对：{error}": "Invalid person masks: {error}",
    "kind 只能是 auto / photo / anime": "kind must be auto / photo / anime",
    "剩余显存 {free} MB，只够 {n} 路": "{free} MB of VRAM free, enough for {n} at once",
    "可用内存 {free} MB，只够 {n} 路": "{free} MB of RAM available, enough for {n} at once",
    "CPU {cores} 线程，最多 {n} 路": "{cores} CPU threads, at most {n} at once",
    "人物序号超出范围（共 {count} 人）": "Person index out of range ({count} in total)",
    "找不到 SAM 3D Body 源码：{path}，请先运行 scripts/setup_sam3d.ps1": "SAM 3D Body source not found: {path}; run scripts/setup_sam3d.ps1 first",
    "找不到权重：{ckpt} / {mhr}，请先运行 scripts/setup_sam3d.ps1": "Weights not found: {ckpt} / {mhr}; run scripts/setup_sam3d.ps1 first",
}
