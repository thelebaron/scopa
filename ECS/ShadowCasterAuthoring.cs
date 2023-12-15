using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scopa
{
    public class ShadowCasterAuthoring : MonoBehaviour
    {
        
    }
    
    internal struct ShadowCasterTag : IComponentData
    {
        
    }
    public class ShadowCasterBaker : Baker<ShadowCasterAuthoring>
    {
        public override void Bake(ShadowCasterAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Renderable);
            
            AddComponent<ShadowCasterTag>(entity);
        }
    }
        
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [UpdateInGroup(typeof(PostBakingSystemGroup))]
    public partial struct ShadowCasterBakingSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (materialMeshInfo, renderMeshArray, renderFilterSettings, entity) in SystemAPI.Query<MaterialMeshInfo, RenderMeshArray, RenderFilterSettings>().WithAll<ShadowCasterTag>().WithEntityAccess())
            {
                var filtersettings = renderFilterSettings;
                filtersettings.ShadowCastingMode  = ShadowCastingMode.ShadowsOnly;
                filtersettings.StaticShadowCaster = true;

                ecb.SetSharedComponent(entity, filtersettings);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}