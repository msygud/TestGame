using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;

namespace Game.Unit.Tests
{
    public sealed class UnitPathfindingTests
    {
        [Test]
        public void TryBuildPath_WithoutObstacles_ReturnsDirectPath()
        {
            var path = new NativeList<float3>(Allocator.Temp);
            var obstacleTransforms = new NativeArray<LocalTransform>(0, Allocator.Temp);
            var obstacleFootprints = new NativeArray<ObstacleFootprint>(0, Allocator.Temp);

            bool found = UnitPathfinding.TryBuildPath(
                CreateGrid(),
                new float3(0.5f, 0f, 0.5f),
                new float3(5.5f, 0f, 0.5f),
                0.4f,
                obstacleTransforms,
                obstacleFootprints,
                path,
                out bool reachedTarget);

            Assert.That(found, Is.True);
            Assert.That(reachedTarget, Is.True);
            Assert.That(path.Length, Is.EqualTo(2));
            Assert.That(path[0].x, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(path[1].x, Is.EqualTo(5.5f).Within(0.001f));

            path.Dispose();
            obstacleFootprints.Dispose();
            obstacleTransforms.Dispose();
        }

        [Test]
        public void TryBuildPath_WithBlockingObstacle_RoutesAroundIt()
        {
            var path = new NativeList<float3>(Allocator.Temp);
            var obstacleTransforms = new NativeArray<LocalTransform>(1, Allocator.Temp);
            var obstacleFootprints = new NativeArray<ObstacleFootprint>(1, Allocator.Temp);
            obstacleTransforms[0] = CreateTransform(new float3(2.5f, 0f, 0.5f));
            obstacleFootprints[0] = new ObstacleFootprint
            {
                Size = new float2(1f, 1f),
                Radius = 0.6f,
                ExtraPadding = 0f,
            };

            bool found = UnitPathfinding.TryBuildPath(
                CreateGrid(),
                new float3(0.5f, 0f, 0.5f),
                new float3(6.5f, 0f, 0.5f),
                0.35f,
                obstacleTransforms,
                obstacleFootprints,
                path,
                out bool reachedTarget);

            Assert.That(found, Is.True);
            Assert.That(reachedTarget, Is.True);
            Assert.That(path.Length, Is.GreaterThan(2));

            path.Dispose();
            obstacleFootprints.Dispose();
            obstacleTransforms.Dispose();
        }

        [Test]
        public void TryBuildPath_WhenTargetCellIsBlocked_ReturnsPartialPath()
        {
            var path = new NativeList<float3>(Allocator.Temp);
            var obstacleTransforms = new NativeArray<LocalTransform>(1, Allocator.Temp);
            var obstacleFootprints = new NativeArray<ObstacleFootprint>(1, Allocator.Temp);
            obstacleTransforms[0] = CreateTransform(new float3(5.5f, 0f, 0.5f));
            obstacleFootprints[0] = new ObstacleFootprint
            {
                Size = new float2(1f, 1f),
                Radius = 0.55f,
                ExtraPadding = 0f,
            };

            bool found = UnitPathfinding.TryBuildPath(
                CreateGrid(),
                new float3(0.5f, 0f, 0.5f),
                new float3(5.5f, 0f, 0.5f),
                0.25f,
                obstacleTransforms,
                obstacleFootprints,
                path,
                out bool reachedTarget);

            Assert.That(found, Is.True);
            Assert.That(reachedTarget, Is.False);
            Assert.That(path.Length, Is.GreaterThan(1));

            path.Dispose();
            obstacleFootprints.Dispose();
            obstacleTransforms.Dispose();
        }

        static UnitNavigationGrid CreateGrid()
        {
            return new UnitNavigationGrid
            {
                Origin = float3.zero,
                Size = new int2(8, 6),
                CellSize = 1f,
                MaxSearchNodes = 128,
            };
        }

        static LocalTransform CreateTransform(float3 position)
        {
            return new LocalTransform
            {
                Position = position,
                Rotation = quaternion.identity,
                Scale = 1f,
            };
        }
    }
}
