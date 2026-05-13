using Game.Unit;
using Unity.Burst;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.XR;

namespace Game.Combat
{/*
    [UpdateInGroup(typeof(LateSimulationSystemGroup))]
    partial struct SystemApplyVisibleity : ISystem
    {
        ComponentLookup<MaterialMeshInfo> _lookupMesh;
        ComponentLookup<OriginalMeshInfo> originalMeshLookup;
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _lookupMesh=state.GetComponentLookup<MaterialMeshInfo>();
            originalMeshLookup=state.GetComponentLookup<OriginalMeshInfo>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _lookupMesh.Update(ref state);
            originalMeshLookup.Update(ref state);

            EntityCommandBuffer ecbbuff = SystemAPI.GetSingleton<BeginPresentationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);

            JobApplyVisible job = new JobApplyVisible() { lookupMaterial = _lookupMesh, lookupOriginal = originalMeshLookup ,ecb=ecbbuff.AsParallelWriter()};
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {

        }
    }
    
    public partial struct JobApplyVisible : IJobEntity
    {
        public ComponentLookup<MaterialMeshInfo> lookupMaterial;
        public ComponentLookup<OriginalMeshInfo> lookupOriginal;

        public EntityCommandBuffer.ParallelWriter ecb;
        public void Execute([ChunkIndexInQuery]int index, EnabledRefRW<RequestRendarable> renderable,ref VisibleOnScreenData isvisible,ref DynamicBuffer<Child> children)
        {
            if(isvisible.Visable)
            {
                if(!renderable.ValueRW)
                {
                    for(int i = 0;children.Length > i;i++)
                    {
                        var child = children[i];
                        var mat = lookupMaterial.GetRefRW(child.Value);
                        if (mat.IsValid)
                        {
                            var mehsid = lookupOriginal.GetRefRO(child.Value);
                            if (mehsid.IsValid)
                            {
                                MaterialMeshInfo tmp = new MaterialMeshInfo();
                                tmp = mat.ValueRO;
                                tmp.Mesh= mehsid.ValueRO.MeshID;
                                ecb.SetComponent<MaterialMeshInfo>(index, child.Value, tmp);
                                renderable.ValueRW = true;
                            }
                        }
                    }
                }
            }
            else
            {
                if(renderable.ValueRW)
                {
                    for (int i = 0; children.Length > i; i++)
                    {
                        var child = children[i];
                        var mat = lookupMaterial.GetRefRW(child.Value);
                        if (mat.IsValid)
                        {
                            MaterialMeshInfo tmp= new MaterialMeshInfo();
                            tmp = mat.ValueRO;
                            tmp.Mesh = 0;
                            ecb.SetComponent<MaterialMeshInfo>(index,child.Value, tmp);
                            renderable.ValueRW = false;
                        }
                    }
                }
            }
            isvisible.Visable = false;
        }
    }*/

}
