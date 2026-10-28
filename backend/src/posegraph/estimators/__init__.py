from .base import EstimateResult, HumanEstimate, PoseEstimator

__all__ = ["EstimateResult", "HumanEstimate", "PoseEstimator", "create_estimator"]


def create_estimator(name: str = "sam3d_body") -> PoseEstimator:
    from ..config import settings

    if name == "sam3d_body":
        from .sam3d_body import Sam3dBodyEstimator

        return Sam3dBodyEstimator(
            repo_dir=settings.sam3d_repo,
            checkpoint_dir=settings.sam3d_checkpoint_dir,
            device=settings.device,
            precision=settings.precision,
            mhr_on_cpu=settings.mhr_on_cpu,
            min_free_vram_mb=settings.min_free_vram_mb,
            fast=settings.fast,
            backbone_scale=settings.backbone_scale,
        )
    raise ValueError(f"未知的估计器：{name}")
