using UnityEngine;
using UnityEngine.Rendering;

namespace Scopa.Editor
{
    public class DisplayMeshInfo : MonoBehaviour
    {
        public IndexFormat           meshFormat;
        public int                   vertexAttributeCount;
        public GraphicsBuffer.Target indexBufferTarget;


        [ContextMenu("Get Mesh Info")]
        void GetMeshInfo()
        {
            var mesh = GetComponent<MeshFilter>().sharedMesh;
            meshFormat = mesh.indexFormat;
            vertexAttributeCount = mesh.vertexAttributeCount;
            indexBufferTarget = mesh.indexBufferTarget;
        }
    }
}