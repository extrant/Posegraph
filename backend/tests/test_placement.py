"""合并模式站位复刻的换算（placement.py），用简化的人物数据，不需要模型。"""
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

import numpy as np
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import placement

NAMES = ["l_upleg", "r_upleg", "c_neck"]


def person(shift=(0.0, 0.0, 0.0), model=None):
    """一个站着的人：镜头系（x 右、y 下、z 向前）里髋宽 0.2 米、颈在髋上方 0.5 米，离镜头 3 米；shift 为镜头系平移。"""
    human = {"joint_coords": np.array([[-0.1, 0.0, 0.0], [0.1, 0.0, 0.0], [0.0, -0.5, 0.0]]),
             "cam_t": np.array([0.0, 0.0, 3.0]) + np.array(shift), "focal_length": 1000.0}
    # FF14 骨架：模型系（y 上），髋在地面上方 0.9 米，颈比 MHR 高 1.2 倍
    rt = SimpleNamespace(upright_rotation=None,
                         all_positions={"j_asi_a_l": np.array([0.1, 0.9, 0.0]), "j_asi_a_r": np.array([-0.1, 0.9, 0.0]),
                                        "j_kubi": np.array([0.0, 1.5, 0.0])})
    return placement.Person(human, rt, model or placement.ModelTransform(np.zeros(3), R.identity(), np.ones(3)))


class PlacementTests(unittest.TestCase):
    def test_base_actor_stays_and_identical_person_lands_on_it(self):
        base = placement.ModelTransform(np.array([5.0, 1.0, -2.0]), R.from_euler("y", 30, degrees=True), np.ones(3))
        placed = placement.place([person(model=base), person()], 0, NAMES)
        self.assertIs(placed[0], base)
        np.testing.assert_allclose(placed[1].position, base.position, atol=1e-9)
        np.testing.assert_allclose(placed[1].rotation.as_quat(), base.rotation.as_quat(), atol=1e-9)

    def test_moving_the_base_actor_moves_everyone_the_same_way(self):
        people = [person(), person(shift=(0.6, 0.1, 0.4))]
        before = placement.place(people, 0, NAMES)
        world = placement.ModelTransform(np.array([3.0, 0.5, 7.0]), R.from_euler("y", -70, degrees=True), np.ones(3))
        people[0].model = world
        after = placement.place(people, 0, NAMES)
        np.testing.assert_allclose(after[1].position, world.position + world.rotation.apply(before[1].position), atol=1e-9)
        np.testing.assert_allclose((world.rotation * before[1].rotation).as_quat(), after[1].rotation.as_quat(), atol=1e-9)

    def test_right_in_the_photo_is_right_of_the_base_actor_at_ff14_scale(self):
        # 镜头系向右 0.5 米：FF14 体型比例 1.2（颈髋 0.6 / 0.5），模型系里镜头看向 -z，所以右边是 +x
        placed = placement.place([person(), person(shift=(0.5, 0.0, 0.0))], 0, NAMES)
        np.testing.assert_allclose(placed[1].position, [0.6, 0.0, 0.0], atol=1e-9)

    def test_switching_the_base_back_and_forth_does_not_drift(self):
        # 两人体型比例、重力校正都不同（以前按各自的换算，来回换基准会越来越高）
        a = person(model=placement.ModelTransform(np.array([2.0, 0.0, 1.0]), R.from_euler("y", 40, degrees=True), np.ones(3)))
        b = person(shift=(0.4, -0.3, 0.6))
        b.rt.all_positions["j_kubi"] = np.array([0.0, 1.8, 0.0])
        b.rt.upright_rotation = R.from_euler("xz", [10, -6], degrees=True).as_quat()
        a.rt.upright_rotation = R.from_euler("x", -4, degrees=True).as_quat()
        start = (a.model.position.copy(), b.model.position.copy())
        b.model = placement.place([a, b], 0, NAMES)[1]
        first_b = b.model.position.copy()
        for _ in range(10):
            a.model = placement.place([a, b], 1, NAMES)[0]
            b.model = placement.place([a, b], 0, NAMES)[1]
        np.testing.assert_allclose(a.model.position, start[0], atol=1e-9)
        np.testing.assert_allclose(b.model.position, first_b, atol=1e-9)

    def test_applying_again_with_the_same_base_changes_nothing(self):
        people = [person(), person(shift=(0.5, 0.2, -0.3))]
        people[1].model = placement.place(people, 0, NAMES)[1]
        again = placement.place(people, 0, NAMES)[1]
        np.testing.assert_allclose(again.position, people[1].model.position, atol=1e-9)

    def test_actors_are_only_turned_about_the_vertical_axis(self):
        tilted = person(shift=(0.5, 0.0, 0.0))
        tilted.rt.upright_rotation = R.from_euler("xyz", [12, 25, -8], degrees=True).as_quat()
        rotation = placement.place([person(), tilted], 0, NAMES)[1].rotation
        np.testing.assert_allclose(rotation.apply([0.0, 1.0, 0.0]), [0.0, 1.0, 0.0], atol=1e-9)


def body(shift=(0.0, 0.0, 0.0)):
    """带躯干和头的人（胶囊体：髋、躯干、颈、头）。"""
    p = person(shift)
    p.rt.all_positions.update({"j_kosi": np.array([0.0, 1.0, 0.0]), "j_sebo_b": np.array([0.0, 1.2, 0.0]),
                               "j_sebo_c": np.array([0.0, 1.35, 0.0]), "j_kao": np.array([0.0, 1.6, 0.0])})
    return p


class GroundTests(unittest.TestCase):
    def test_everyone_is_put_on_the_base_actors_floor(self):
        def seated(low):
            p = person()
            p.rt.all_positions.update({"j_asi_e_l": np.array([0.2, low, 0.3]), "j_kami_a": np.array([0.0, -1.0, 0.0])})
            return p
        people = [seated(0.02), seated(0.10)]
        placed = [placement.ModelTransform(np.array([0.0, 10.0, 0.0]), R.identity(), np.ones(3)),
                  placement.ModelTransform(np.array([1.0, 10.55, 2.0]), R.from_euler("y", 40, degrees=True), np.ones(3))]
        placement.ground(people, placed, 0)
        self.assertAlmostEqual(placement._lowest(people[1], placed[1]), placement._lowest(people[0], placed[0]))
        np.testing.assert_allclose(placed[1].position[[0, 2]], [1.0, 2.0])   # 只上下挪
        np.testing.assert_allclose(placed[0].position, [0.0, 10.0, 0.0])   # 基准不动；头发骨不算最低点


class ManualOffsetTests(unittest.TestCase):
    def test_manual_offset_survives_placement_and_grounding(self):
        # 调整页的整体位移：摆站位、放到地面后都还在（不被当成照片里的位置抵消掉）
        plain, moved = person(shift=(0.4, 0.0, 0.0)), person(shift=(0.4, 0.0, 0.0))
        offset = np.array([0.05, 0.10, -0.20])
        moved.rt.all_positions = {n: v + offset for n, v in moved.rt.all_positions.items()}
        moved.rt.root_offset = offset
        a = placement.place([person(), plain], 0, NAMES)
        b = placement.place([person(), moved], 0, NAMES)
        np.testing.assert_allclose(b[1].position, a[1].position, atol=1e-9)
        placement.ground([person(), moved], b, 0)
        world = lambda m, v: m.position + m.rotation.apply(v)
        lowest = min(world(b[1], v)[1] for v in moved.rt.all_positions.values())
        self.assertAlmostEqual(lowest - min(world(b[0], v)[1] for v in person().rt.all_positions.values()), 0.10)


def ball(center, radius, rings=16, segments=24):
    """经纬球网格（各个球拓扑相同，可以共用三角形）。"""
    verts = [[0, -radius, 0]]
    for i in range(1, rings):
        a = np.pi * i / rings
        for j in range(segments):
            b = 2 * np.pi * j / segments
            verts.append([radius * np.sin(a) * np.cos(b), -radius * np.cos(a), radius * np.sin(a) * np.sin(b)])
    verts.append([0, radius, 0])
    faces = []
    ring = lambda i, j: 1 + (i - 1) * segments + j % segments
    for j in range(segments):
        faces.append([0, ring(1, j + 1), ring(1, j)])
        faces.append([len(verts) - 1, ring(rings - 1, j), ring(rings - 1, j + 1)])
    for i in range(1, rings - 1):
        for j in range(segments):
            a, b, c, d = ring(i, j), ring(i, j + 1), ring(i + 1, j + 1), ring(i + 1, j)
            faces += [[a, b, c], [a, c, d]]
    return np.array(verts) + center, np.array(faces)


if __name__ == "__main__":
    unittest.main()
