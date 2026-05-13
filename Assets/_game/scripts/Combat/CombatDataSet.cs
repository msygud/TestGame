using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Game.Combat
{
    public class CombatDataSet
    {

    }
    public struct UnitGridIndex : IComponentData
    {
        public int2 GridIndex;
    }
    public struct DiryGrid : IComponentData
    {
    }
    public struct VisibleStateData : IComponentData
    {
        public enum State { FullVisible,RealVisible}
        public State Visible;
    }
    public struct CombatGridInfo : IComponentData
    {
        public float2 MapSize;
        public float2 MinPosition;
        public float2 MaxPosition;
        public int2 GridCount;
        public float Cellsize;
    }
}