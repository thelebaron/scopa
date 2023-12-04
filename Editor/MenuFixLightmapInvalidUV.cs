using UnityEditor;
using UnityEngine;

namespace Scopa.Editor
{
    public static class MenuFixLightmapInvalidUV
    {
        [MenuItem("Tools/Check and fix Invalid uv mesh")]
        private static void CheckAndFixMesh()
        {
            //Fix uv issues so that lightmapper can work!
            var meshFilter = Selection.activeGameObject.GetComponent<MeshFilter>();
            var meshUV     = meshFilter.sharedMesh.uv;
            var meshUV2    = meshFilter.sharedMesh.uv2;
            var meshVertices = meshFilter.sharedMesh.vertices;
            var meshNormals  = meshFilter.sharedMesh.normals;
            var tangents     = meshFilter.sharedMesh.tangents;
            
            var       assignBack = false;
            
            // check for NaN values in vertices
            for (int i = 0; i < meshFilter.sharedMesh.vertices.Length; i++) 
            {
                if ((float.IsNaN(meshFilter.sharedMesh.vertices[i].x)) || (float.IsNaN(meshFilter.sharedMesh.vertices[i].y)) || (float.IsNaN(meshFilter.sharedMesh.vertices[i].z)))
                {
                    assignBack = true;
                    Debug.LogError("Vertex Float error:" + i  + " v:" + meshFilter.sharedMesh.vertices[i].ToString("F4"));
                    meshVertices[i] = Vector3.zero;
                }          
            }
            // check for NaN values in normals
            for (int i = 0; i < meshNormals.Length; i++) 
            {
                if ((float.IsNaN(meshNormals[i].x)) || (float.IsNaN(meshNormals[i].y)) || (float.IsNaN(meshNormals[i].z)))
                {
                    assignBack = true;
                    Debug.LogError("Normal Float error:" + i  + " v:" + meshNormals[i].ToString("F4"));
                    meshNormals[i] = Vector3.zero;
                }          
            }
            
            // check tangents
            for (int i = 0; i < tangents.Length; i++) 
            {
                if ((float.IsNaN(tangents[i].x)) || (float.IsNaN(tangents[i].y)) || (float.IsNaN(tangents[i].z)) || (float.IsNaN(tangents[i].w)))
                {
                    assignBack = true;
                    Debug.LogError("Tangent Float error:" + i  + " v:" + tangents[i].ToString("F4"));
                    tangents[i] = Vector4.zero;
                }          
            }
            
            
            // check for NaN values in UVs
            for (int i = 0; i < meshUV.Length; i++) 
            {
                if ((float.IsNaN(meshUV[i].x)) || (float.IsNaN(meshUV[i].y)))
                {
                    assignBack = true;
                    Debug.LogError("UV Float error:" + i  + " v:" + meshUV[i].ToString("F4"));
                    meshUV[i] = Vector2.zero;
                }          
            }
             
            // check for NaN values in UVs2
            for (int i = 0; i < meshUV2.Length; i++) 
            {
                if ((float.IsNaN(meshUV2[i].x)) || (float.IsNaN(meshUV2[i].y)))
                {
                    assignBack = true;
                    Debug.LogError("UV2 Float error:" + i  + " v:" + meshUV2[i].ToString("F4"));
                    meshUV2[i] = Vector2.zero;
                }          
            }
            
            if (assignBack) 
            {
                Debug.Log("Fix mesh!");
                meshFilter.mesh.uv = meshUV;
                meshFilter.mesh.uv2 = meshUV2;
                meshFilter.mesh.vertices = meshVertices;
                meshFilter.mesh.normals = meshNormals;
                meshFilter.mesh.tangents = tangents;
                
            } else {
                Debug.Log("Mesh is ok!");
            }
        }

    }
}