using UnityEngine;

namespace Scopa
{
    [ExecuteAlways]
    public class ScopaUVRotationTester : MonoBehaviour
    {
        public float AngleDegrees = 90;

        [ContextMenu("Rotate")]
        public void Rotate()
        {
            if(GetComponent<MeshFilter>() == null) return;
            var mesh = GetComponent<MeshFilter>().sharedMesh;
            if (mesh == null) return;
            
            // copy mesh to new mesh
            var newmesh = Instantiate(mesh);
            
            // rotate uvs by angle
            var uvs   = newmesh.uv;
            var angle = AngleDegrees * Mathf.Deg2Rad;
            var cos   = Mathf.Cos(angle);
            var sin   = Mathf.Sin(angle);
            for (var i = 0; i < uvs.Length; i++)
            {
                var uv = uvs[i];
                var x = uv.x - 0.5f;
                var y = uv.y - 0.5f;
                uv.x = x * cos - y * sin + 0.5f;
                uv.y = x * sin + y * cos + 0.5f;
                uvs[i] = uv;
            }
            
            // assign new mesh
            GetComponent<MeshFilter>().sharedMesh = newmesh;
            
            newmesh.uv = uvs;
            newmesh.RecalculateNormals();
            newmesh.RecalculateTangents();
            
        }
    }
}