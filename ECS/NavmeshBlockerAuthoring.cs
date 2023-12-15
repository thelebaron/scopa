using Unity.AI.Navigation;

namespace Scopa
{
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Entities.Graphics;
    using Unity.Rendering;
    using UnityEngine;
    using UnityEngine.Rendering;
    
    namespace Scopa
    {
        [RequireComponent(typeof(NavMeshSurface))]
        public class NavmeshBlockerAuthoring : MonoBehaviour
        {
            // todo insert self into ignore collider for scopa map
        }
        
        internal struct NavmeshBlockerTag : IComponentData
        {
            
        }
        
        public class NavmeshBlockerBaker : Baker<NavmeshBlockerAuthoring>
        {
            public override void Bake(NavmeshBlockerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Renderable);
                
                AddComponent<NavmeshBlockerTag>(entity);
            }
        }
        
        
        [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
        [UpdateInGroup(typeof(PostBakingSystemGroup))]
        public partial struct NavmeshBlockerBakingSystem : ISystem
        {
            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                var ecb = new EntityCommandBuffer(Allocator.Temp);
                foreach (var (materialMeshInfo, renderFilterSettings, entity) in SystemAPI.Query<MaterialMeshInfo, RenderFilterSettings>().WithAll<NavmeshBlockerTag>().WithEntityAccess())
                {
                    ecb.AddComponent<DisableRendering>(entity);
                }
    
                ecb.Playback(state.EntityManager);
                ecb.Dispose();
            }
        }
    }
}