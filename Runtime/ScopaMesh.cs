using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Sledge.Formats.Map.Formats;
using Sledge.Formats.Map.Objects;
using Sledge.Formats.Precision;
using Scopa.Formats.Texture.Wad;
using Scopa.Formats.Id;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine.Rendering;
using Mesh = UnityEngine.Mesh;
using Vector3 = UnityEngine.Vector3;

#if SCOPA_USE_BURST
using Unity.Burst;
using Unity.Mathematics;
#endif

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Scopa {
    /// <summary>main class for Scopa mesh generation / geo functions</summary>
    public static class ScopaMesh {
        // to avoid GC, we use big static lists so we just allocate once
        // TODO: will have to reorganize this for multithreading later on
        static List<Face> allFaces = new List<Face>(8192);
        static HashSet<Face> discardedFaces = new HashSet<Face>(8192);

        static                  List<Vector3> verts     = new List<Vector3>(4096);
        static                  List<Vector3> faceVerts = new List<Vector3>(64);
        static                  List<int>     tris      = new List<int>(8192);
        static                  List<int>     faceTris  = new List<int>(32);
        static                  List<Vector2> uvs       = new List<Vector2>(4096);
        static                  List<Vector2> faceUVs   = new List<Vector2>(64);
        private static readonly int           BaseMap   = Shader.PropertyToID("_BaseMap");

        const float EPSILON = 0.01f;


        public static void AddFaceForCulling(Face brushFace) {
            allFaces.Add(brushFace);
        }

        public static void ClearFaceCullingList() {
            allFaces.Clear();
            discardedFaces.Clear();
        }

        public static void DiscardFace(Face brushFace) {
            discardedFaces.Add(brushFace);
        }

        public static bool IsFaceCulledDiscard(Face brushFace) {
            return discardedFaces.Contains(brushFace);
        }
        
        public static FaceCullingJobGroup StartFaceCullingJobs() {
            return new FaceCullingJobGroup();
        }

        public class FaceCullingJobGroup {
            NativeArray<int> cullingOffsets;
            NativeArray<Vector4> cullingPlanes;
            NativeArray<Vector3> cullingVerts;
            NativeArray<bool> cullingResults;
            JobHandle jobHandle;

            public FaceCullingJobGroup() {
                var vertCount = 0;
                cullingOffsets = new NativeArray<int>(allFaces.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                cullingPlanes = new NativeArray<Vector4>(allFaces.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for(int i=0; i<allFaces.Count; i++) {
                    cullingOffsets[i] = vertCount;
                    vertCount += allFaces[i].Vertices.Count;
                    cullingPlanes[i] = new Vector4(allFaces[i].Plane.Normal.X, allFaces[i].Plane.Normal.Y, allFaces[i].Plane.Normal.Z, allFaces[i].Plane.D);
                }

                cullingVerts = new NativeArray<Vector3>(vertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for(int i=0; i<allFaces.Count; i++) {
                    for(int v=cullingOffsets[i]; v < (i<cullingOffsets.Length-1 ? cullingOffsets[i+1] : vertCount); v++) {
                        cullingVerts[v] = allFaces[i].Vertices[v-cullingOffsets[i]].ToUnity();
                    }
                }
                
                cullingResults = new NativeArray<bool>(allFaces.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for(int i=0; i<allFaces.Count; i++) {
                    cullingResults[i] = IsFaceCulledDiscard(allFaces[i]);
                }
                
                var jobData = new FaceCullingJob();

                #if SCOPA_USE_BURST
                jobData.faceVertices = cullingVerts.Reinterpret<float3>();
                jobData.facePlanes = cullingPlanes.Reinterpret<float4>();
                #else
                jobData.faceVertices = cullingVerts;
                jobData.facePlanes = cullingPlanes;
                #endif

                jobData.faceVertexOffsets = cullingOffsets;
                jobData.cullFaceResults = cullingResults;
                jobHandle = jobData.Schedule(cullingResults.Length, 32);
            }

            public void Complete() {
                jobHandle.Complete();

                // int culledFaces = 0;
                for(int i=0; i<cullingResults.Length; i++) {
                    // if (!allFaces[i].discardWhenBuildingMesh && cullingResults[i])
                    //     culledFaces++;
                    if(cullingResults[i])
                        discardedFaces.Add(allFaces[i]);
                }
                // Debug.Log($"Culled {culledFaces} faces!");

                cullingOffsets.Dispose();
                cullingVerts.Dispose();
                cullingPlanes.Dispose();
                cullingResults.Dispose();
            }

        }

        #if SCOPA_USE_BURST
        [BurstCompile]
        #endif
        public struct FaceCullingJob : IJobParallelFor
        {
            [ReadOnlyAttribute]
            #if SCOPA_USE_BURST
            public NativeArray<float3> faceVertices;
            #else
            public NativeArray<Vector3> faceVertices;
            #endif   

            [ReadOnlyAttribute]
            #if SCOPA_USE_BURST
            public NativeArray<float4> facePlanes;
            #else
            public NativeArray<Vector4> facePlanes;
            #endif

            [ReadOnlyAttribute]
            public NativeArray<int> faceVertexOffsets;
            
            public NativeArray<bool> cullFaceResults;

            public void Execute(int i)
            {
                if (cullFaceResults[i])
                    return;

                // test against all other faces
                for(int n=0; n<faceVertexOffsets.Length; n++) {
                    // first, test (1) share similar plane distance and (2) face opposite directions
                    // we are testing the NEGATIVE case for early out
                    #if SCOPA_USE_BURST
                    if ( math.abs(facePlanes[i].w + facePlanes[n].w) > 0.5f || math.dot(facePlanes[i].xyz, facePlanes[n].xyz) > -0.999f )
                        continue;
                    #else
                    if ( Mathf.Abs(facePlanes[i].w + facePlanes[n].w) > 0.5f || Vector3.Dot(facePlanes[i], facePlanes[n]) > -0.999f )
                        continue;
                    #endif
                    
                    // then, test whether this face's vertices are completely inside the other
                    var offsetStart = faceVertexOffsets[i];
                    var offsetEnd = i<faceVertexOffsets.Length-1 ? faceVertexOffsets[i+1] : faceVertices.Length;

                    var Center = faceVertices[offsetStart];
                    for( int b=offsetStart+1; b<offsetEnd; b++) {
                        Center += faceVertices[b];
                    }
                    Center /= offsetEnd-offsetStart;

                    // 2D math is easier, so let's ignore the least important axis
                    var ignoreAxis = GetMainAxisToNormal(facePlanes[i]);

                    var otherOffsetStart = faceVertexOffsets[n];
                    var otherOffsetEnd = n<faceVertexOffsets.Length-1 ? faceVertexOffsets[n+1] : faceVertices.Length;

                    #if SCOPA_USE_BURST
                    var polygon = new NativeArray<float3>(otherOffsetEnd-otherOffsetStart, Allocator.Temp);
                    NativeArray<float3>.Copy(faceVertices, otherOffsetStart, polygon, 0, polygon.Length);
                    #else
                    var polygon = new Vector3[otherOffsetEnd-otherOffsetStart];
                    NativeArray<Vector3>.Copy(faceVertices, otherOffsetStart, polygon, 0, polygon.Length);
                    #endif

                    var vertNotInOtherFace = false;
                    for( int x=offsetStart; x<offsetEnd; x++ ) {
                        #if SCOPA_USE_BURST
                        var p = faceVertices[x] + math.normalize(Center - faceVertices[x]) * 0.2f;
                        #else
                        var p = faceVertices[x] + Vector3.Normalize(Center - faceVertices[x]) * 0.2f;
                        #endif                  
                        switch (ignoreAxis) {
                            case Axis.X: if (!IsInPolygonYZ(p, polygon)) vertNotInOtherFace = true; break;
                            case Axis.Y: if (!IsInPolygonXZ(p, polygon)) vertNotInOtherFace = true; break;
                            case Axis.Z: if (!IsInPolygonXY(p, polygon)) vertNotInOtherFace = true; break;
                        }

                        if (vertNotInOtherFace)
                            break;
                    }

                    #if SCOPA_USE_BURST
                    polygon.Dispose();
                    #endif

                    if (vertNotInOtherFace)
                        continue;

                    // if we got this far, then this face should be culled
                    var tempResult = true;
                    cullFaceResults[i] = tempResult;
                    return;
                }
               
            }
        }

        public enum Axis { X, Y, Z}

        #if SCOPA_USE_BURST
        public static Axis GetMainAxisToNormal(float4 vec) {
            // VHE prioritises the axes in order of X, Y, Z.
            // so in Unity land, that's X, Z, and Y
            var norm = new float3(
                math.abs(vec.x), 
                math.abs(vec.y),
                math.abs(vec.z)
            );

            if (norm.x >= norm.y && norm.x >= norm.z) return Axis.X;
            if (norm.z >= norm.y) return Axis.Z;
            return Axis.Y;
        }

        public static bool IsInPolygonXY(float3 p, NativeArray<float3> polygon ) {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for ( int i = 0, j = polygon.Length - 1 ; i < polygon.Length ; j = i++ ) {
                if ( ( polygon[i].y > p.y ) != ( polygon[j].y > p.y ) &&
                    p.x < ( polygon[j].x - polygon[i].x ) * ( p.y - polygon[i].y ) / ( polygon[j].y - polygon[i].y ) + polygon[i].x )
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public static bool IsInPolygonYZ(float3 p, NativeArray<float3> polygon ) {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for ( int i = 0, j = polygon.Length - 1 ; i < polygon.Length ; j = i++ ) {
                if ( ( polygon[i].y > p.y ) != ( polygon[j].y > p.y ) &&
                    p.z < ( polygon[j].z - polygon[i].z ) * ( p.y - polygon[i].y ) / ( polygon[j].y - polygon[i].y ) + polygon[i].z )
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public static bool IsInPolygonXZ(float3 p, NativeArray<float3> polygon ) {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for ( int i = 0, j = polygon.Length - 1 ; i < polygon.Length ; j = i++ ) {
                if ( ( polygon[i].x > p.x ) != ( polygon[j].x > p.x ) &&
                    p.z < ( polygon[j].z - polygon[i].z ) * ( p.x - polygon[i].x ) / ( polygon[j].x - polygon[i].x ) + polygon[i].z )
                {
                    inside = !inside;
                }
            }

            return inside;
        }
        #endif

        public static Axis GetMainAxisToNormal(Vector3 norm) {
            // VHE prioritises the axes in order of X, Y, Z.
            // so in Unity land, that's X, Z, and Y
            norm = norm.Absolute();

            if (norm.x >= norm.y && norm.x >= norm.z) return Axis.X;
            if (norm.z >= norm.y) return Axis.Z;
            return Axis.Y;
        }

        public static bool IsInPolygonXY( Vector3 p, Vector3[] polygon ) {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for ( int i = 0, j = polygon.Length - 1 ; i < polygon.Length ; j = i++ ) {
                if ( ( polygon[i].y > p.y ) != ( polygon[j].y > p.y ) &&
                    p.x < ( polygon[j].x - polygon[i].x ) * ( p.y - polygon[i].y ) / ( polygon[j].y - polygon[i].y ) + polygon[i].x )
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public static bool IsInPolygonYZ( Vector3 p, Vector3[] polygon ) {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for ( int i = 0, j = polygon.Length - 1 ; i < polygon.Length ; j = i++ ) {
                if ( ( polygon[i].y > p.y ) != ( polygon[j].y > p.y ) &&
                    p.z < ( polygon[j].z - polygon[i].z ) * ( p.y - polygon[i].y ) / ( polygon[j].y - polygon[i].y ) + polygon[i].z )
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public static bool IsInPolygonXZ( Vector3 p, Vector3[] polygon ) {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for ( int i = 0, j = polygon.Length - 1 ; i < polygon.Length ; j = i++ ) {
                if ( ( polygon[i].x > p.x ) != ( polygon[j].x > p.x ) &&
                    p.z < ( polygon[j].z - polygon[i].z ) * ( p.x - polygon[i].x ) / ( polygon[j].x - polygon[i].x ) + polygon[i].z )
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public class MeshBuildingJobGroup 
        {

            NativeArray<int>     faceVertexOffsets, faceTriIndexCounts; // index = i
            NativeArray<Vector3> faceVertices;
            NativeArray<Vector4> faceU, faceV; // index = i, .w = scale
            NativeArray<Vector2> faceShift; // index = i
            NativeArray<float>   faceAngle;
            NativeArray<float3>  faceNormals;
            int                  vertCount, triIndexCount;

            public Mesh.MeshDataArray outputMesh;
            Mesh newMesh;
            JobHandle jobHandle;

            public MeshBuildingJobGroup(string meshName, Vector3 meshOrigin, IEnumerable<Solid> solids, ScopaMapConfig config, ScopaMapConfig.MaterialOverride textureFilter = null, bool includeDiscardedFaces = false) 
            {
                var faceList = new List<Face>();
                foreach( var solid in solids) 
                {
                    foreach(var face in solid.Faces) 
                    {
                        // if ( face.Vertices == null || face.Vertices.Count == 0) // this shouldn't happen though
                        //     continue;

                        if ( !includeDiscardedFaces && IsFaceCulledDiscard(face) )
                            continue;

                        if ( textureFilter != null && textureFilter.textureName.ToLowerInvariant().GetHashCode() != face.TextureName.GetHashCode() )
                            continue;

                        faceList.Add(face);
                    }
                }

                faceVertexOffsets = new NativeArray<int>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                faceTriIndexCounts = new NativeArray<int>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for(int i=0; i<faceList.Count; i++) 
                {
                    faceVertexOffsets[i] = vertCount;
                    vertCount += faceList[i].Vertices.Count;
                    faceTriIndexCounts[i] = triIndexCount;
                    triIndexCount += (faceList[i].Vertices.Count-2)*3;
                }

                faceVertices = new NativeArray<Vector3>(vertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                faceU = new NativeArray<Vector4>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                faceV = new NativeArray<Vector4>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                faceShift = new NativeArray<Vector2>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                faceAngle = new NativeArray<float>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                faceNormals = new NativeArray<float3>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                
                // try add rotation to uvs
                for(int i=0; i<faceList.Count; i++) 
                {
                    for(int v=faceVertexOffsets[i]; v < (i<faceVertexOffsets.Length-1 ? faceVertexOffsets[i+1] : vertCount); v++)
                    {
                        faceVertices[v] = faceList[i].Vertices[v-faceVertexOffsets[i]].ToUnity();
                    }

                    var f = faceList[i];
                    var r = faceList[i].Rotation;
                    
                    faceU[i] = new Vector4(faceList[i].UAxis.X, faceList[i].UAxis.Y, faceList[i].UAxis.Z, faceList[i].XScale);
                    faceV[i] = new Vector4(faceList[i].VAxis.X, faceList[i].VAxis.Y, faceList[i].VAxis.Z, faceList[i].YScale);
                    faceShift[i] = new Vector2(faceList[i].XShift, faceList[i].YShift);
                    faceAngle[i] = faceList[i].Rotation;
                    faceNormals[i] = new float3(faceList[i].Plane.Normal.X, faceList[i].Plane.Normal.Y, faceList[i].Plane.Normal.Z);
                }

                outputMesh = Mesh.AllocateWritableMeshData(1);
                var meshData = outputMesh[0];
                meshData.SetVertexBufferParams(vertCount,
                    new VertexAttributeDescriptor(VertexAttribute.Position),
                    new VertexAttributeDescriptor(VertexAttribute.Normal, stream:1),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, dimension:2, stream:2)
                );
                meshData.SetIndexBufferParams(triIndexCount, IndexFormat.UInt32);
                 
                var meshBuildingJob = new MeshBuildingJob
                {
                    faceVertexOffsets  = faceVertexOffsets,
                    faceTriIndexCounts = faceTriIndexCounts,
                    faceVertices       = faceVertices.Reinterpret<float3>(),
                    faceU              = faceU.Reinterpret<float4>(),
                    faceV              = faceV.Reinterpret<float4>(),
                    faceShift          = faceShift.Reinterpret<float2>(),
                    faceAngle          = faceAngle,
                    FaceNormals        = faceNormals,
                    meshData           = outputMesh[0],
                    scalingFactor      = config.scalingFactor,
                    globalTexelScale   = config.globalTexelScale
                };

                // Maintexture does not exist in URP
                //jobData.textureWidth = textureFilter?.material?.mainTexture != null ? textureFilter.material.mainTexture.width : config.defaultTexSize;
                //jobData.textureHeight = textureFilter?.material?.mainTexture != null ? textureFilter.material.mainTexture.height : config.defaultTexSize;


                //var hasTexture = textureFilter?.material?.HasTexture(BaseMap) != null;
                //if (hasTexture)
                {
                    //jobData.textureWidth = textureFilter.material.GetTexture(BaseMap) != null ? textureFilter.material.GetTexture(BaseMap).width : config.defaultTexSize;
                }
                // fix for URP, builtin is obsolete for this project
                // todo use ifdef for URP/HDRP/Builtin
                meshBuildingJob.textureWidth = textureFilter?.material?.GetTexture(BaseMap) != null ? textureFilter.material.GetTexture(BaseMap).width : config.defaultTexSize;
                meshBuildingJob.textureHeight = textureFilter?.material?.GetTexture(BaseMap) != null ? textureFilter.material.GetTexture(BaseMap).height : config.defaultTexSize;
                
                meshBuildingJob.meshOrigin = meshOrigin;
                jobHandle = meshBuildingJob.Schedule(faceList.Count, 128);

                newMesh = new Mesh();
                newMesh.name = meshName;
            }

            public Mesh Complete() {
                jobHandle.Complete();

                var meshData = outputMesh[0];
                meshData.subMeshCount = 1;
                meshData.SetSubMesh(0, new SubMeshDescriptor(0, triIndexCount), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);

                Mesh.ApplyAndDisposeWritableMeshData(outputMesh, newMesh);
                newMesh.RecalculateNormals();
                newMesh.RecalculateBounds();

                // ok this is a total hack, create another mesh because there might be unknown issues with the meshdata from the job
                Mesh mesh = new Mesh();
                mesh.vertices  = newMesh.vertices;
                mesh.triangles = newMesh.triangles;
                mesh.uv        = newMesh.uv;
                mesh.uv2       = newMesh.uv2;
                mesh.normals   = newMesh.normals;
                mesh.colors    = newMesh.colors;
                mesh.tangents  = newMesh.tangents;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                
                faceVertexOffsets.Dispose();
                faceTriIndexCounts.Dispose();
                faceVertices.Dispose();
                faceU.Dispose();
                faceV.Dispose();
                faceShift.Dispose();
                faceAngle.Dispose();
                faceNormals.Dispose();

                return mesh;
            }

        }

        #if SCOPA_USE_BURST
        [BurstCompile]
        #endif
        public struct MeshBuildingJob : IJobParallelFor
        {
            [ReadOnlyAttribute] public NativeArray<int> faceVertexOffsets, faceTriIndexCounts; // index = i

            [ReadOnlyAttribute] public NativeArray<float3> faceVertices;
            [ReadOnlyAttribute] public NativeArray<float4> faceU, faceV; // index = i, .w = scale
            [ReadOnlyAttribute] public NativeArray<float2> faceShift; // index = i
            [ReadOnlyAttribute] public NativeArray<float>  faceAngle;
            [ReadOnlyAttribute] public NativeArray<float3>  FaceNormals;
            [ReadOnlyAttribute] public float3              meshOrigin;
            
            [NativeDisableParallelForRestriction]
            public Mesh.MeshData meshData;

            [ReadOnlyAttribute] public float scalingFactor, globalTexelScale, textureWidth, textureHeight;

            public void Execute(int i)
            {
                var offsetStart = faceVertexOffsets[i];
                var offsetEnd = i<faceVertexOffsets.Length-1 ? faceVertexOffsets[i+1] : faceVertices.Length;

                var outputVerts = meshData.GetVertexData<float3>();
                var outputUVs = meshData.GetVertexData<float2>(2);


                var outputTris = meshData.GetIndexData<int>();

                // add all verts, normals, and UVs
                for( int n=offsetStart; n<offsetEnd; n++ ) 
                {
                    outputVerts[n] = faceVertices[n] * scalingFactor - meshOrigin;
                    
                    var uv = float2.zero;
                                            
                    if(faceAngle[i] == 0f || faceAngle[i] % 360 == 0) 
                    {
                        var shift = new float2(
                            (faceShift[i].x % textureWidth),
                            (-faceShift[i].y % textureHeight)
                        );
                        // Calculate U and V without rotation and adjustment first
                        uv = new float2(
                            CalculateUCoordinate(faceVertices[n], faceU[i], faceAngle[i]),
                            CalculateVCoordinate(faceVertices[n], faceV[i], faceAngle[i])
                        );
                        uv += shift;
                        uv /= new float2(textureWidth, textureHeight);
                    
                        uv = RotateUV(uv, faceAngle[i]);

                        // Assign the final UV coordinates to outputUVs[n]
                        outputUVs[n] = uv * globalTexelScale;
                    }
                    
                    // notes - in tb shift x/y is always texture space.
                    else
                    {
                        var shift = new float2(
                            (-faceShift[i].x % textureWidth),
                            (-faceShift[i].y % textureHeight)
                        );
                        // swap shift left to right depending on angle greater than 180
                        if (faceAngle[i] >= 90f)
                        {
                            var newShift = new float2(
                                (-faceShift[i].y % textureHeight),
                                (faceShift[i].x % textureWidth)
                            );
                            shift = newShift;
                        }
                        // Calculate U and V without rotation and adjustment first
                        uv = new float2(
                            CalculateUCoordinate(faceVertices[n], faceU[i], faceAngle[i]),
                            CalculateVCoordinate(faceVertices[n], faceV[i], faceAngle[i])
                        );
                        uv += shift;
                        uv /= new float2(textureWidth, textureHeight);
                    
                        uv = RotateUV(uv, faceAngle[i]);

                        // Assign the final UV coordinates to outputUVs[n]
                        outputUVs[n] = uv * globalTexelScale;
                    }
                    
                    /*
                   // original code from scopa
                   outputUVs[n] = new Vector2(
                       (math.dot(faceVertices[n], faceU[i].xyz / faceU[i].w) + (faceShift[i].x % textureWidth)) / (textureWidth),
                       (math.dot(faceVertices[n], faceV[i].xyz / -faceV[i].w) + (-faceShift[i].y % textureHeight)) / (textureHeight)
                           ) * globalTexelScale;
                   */
                }

                // verts are already in correct order, add as basic fan pattern (since we know it's a convex face)
                for(int t=2; t<offsetEnd-offsetStart; t++) {
                    outputTris[faceTriIndexCounts[i]+(t-2)*3] = offsetStart;
                    outputTris[faceTriIndexCounts[i]+(t-2)*3+1] = offsetStart + t-1;
                    outputTris[faceTriIndexCounts[i]+(t-2)*3+2] = offsetStart + t;
                }
            }
            
            private static float CalculateUCoordinate(float3 vertex, float4 faceU, float angleDegrees) 
            {
                return math.dot(vertex, faceU.xyz / faceU.w);
            }

            private static float CalculateVCoordinate(float3 vertex, float4 faceV, float angleDegrees) 
            {
                return math.dot(vertex, faceV.xyz / -faceV.w);
            }

            private static float2 RotateUV(float2 uv, float angleDegrees) 
            {
                var angle    = math.radians(-angleDegrees);
                var cosAngle = math.cos(angle);
                var sinAngle = math.sin(angle);
    
                // Rotation matrix
                var u = uv.x * cosAngle - uv.y * sinAngle;
                var v = uv.x * sinAngle + uv.y * cosAngle;

                return new float2(u, v);
            }

            public static Vector2 Divide(Vector2 a, Vector2 b)
            {
                return new Vector2(a.x / b.x, a.y / b.y);
            }
        }

        public class ColliderJobGroup {

            NativeArray<int> faceVertexOffsets, faceTriIndexCounts, solidFaceOffsets; // index = i
            NativeArray<Vector3> faceVertices, facePlaneNormals;
            NativeArray<bool> canBeBoxCollider;
            int vertCount, triIndexCount, faceCount;

            public GameObject gameObject;
            public Mesh.MeshDataArray outputMesh;
            JobHandle jobHandle;
            Mesh[] meshes;
            bool isTrigger, isConvex;

            public ColliderJobGroup(GameObject gameObject, bool isTrigger, bool forceConvex, string colliderNameFormat, IEnumerable<Solid> solids, ScopaMapConfig config, Dictionary<Solid, Entity> mergedEntityData) {
                this.gameObject = gameObject;

                var faceList = new List<Face>();
                var solidFaceOffsetsManaged = new List<int>();
                var solidCount = 0;
                this.isTrigger = isTrigger;
                this.isConvex = forceConvex || config.colliderMode != ScopaMapConfig.ColliderImportMode.MergeAllToOneConcaveMeshCollider;
                foreach( var solid in solids) {
                    if (mergedEntityData.ContainsKey(solid) && (config.IsEntityNonsolid(mergedEntityData[solid].ClassName) || config.IsEntityTrigger(mergedEntityData[solid].ClassName)) )
                        continue;

                    foreach(var face in solid.Faces) {
                        faceList.Add(face);
                    }

                    // if forceConvex or MergeAllToOneConcaveMeshCollider, then pretend it's all just one giant brush
                    // unless it's a trigger, then it MUST be convex
                    if (isTrigger || (!forceConvex && config.colliderMode != ScopaMapConfig.ColliderImportMode.MergeAllToOneConcaveMeshCollider) || solidCount == 0) {
                        solidFaceOffsetsManaged.Add(faceCount);
                        solidCount++;
                    }

                    faceCount += solid.Faces.Count;
                }
                solidFaceOffsetsManaged.Add(faceCount);

                solidFaceOffsets = new NativeArray<int>(solidFaceOffsetsManaged.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                solidFaceOffsets.CopyFrom( solidFaceOffsetsManaged.ToArray() );
                canBeBoxCollider = new NativeArray<bool>(solidCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                faceVertexOffsets = new NativeArray<int>(faceList.Count+1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                faceTriIndexCounts = new NativeArray<int>(faceList.Count+1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for(int i=0; i<faceList.Count; i++) {
                    faceVertexOffsets[i] = vertCount;
                    vertCount += faceList[i].Vertices.Count;
                    faceTriIndexCounts[i] = triIndexCount;
                    triIndexCount += (faceList[i].Vertices.Count-2)*3;
                }
                faceVertexOffsets[faceVertexOffsets.Length-1] = vertCount;
                faceTriIndexCounts[faceTriIndexCounts.Length-1] = triIndexCount;

                faceVertices = new NativeArray<Vector3>(vertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                facePlaneNormals = new NativeArray<Vector3>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for(int i=0; i<faceList.Count; i++) {
                    for(int v=faceVertexOffsets[i]; v < faceVertexOffsets[i+1]; v++) {
                        faceVertices[v] = faceList[i].Vertices[v-faceVertexOffsets[i]].ToUnity();
                    }
                    facePlaneNormals[i] = faceList[i].Plane.Normal.ToUnity();
                }

                outputMesh = Mesh.AllocateWritableMeshData(solidCount);
                meshes = new Mesh[solidCount];
                for(int i=0; i<solidCount; i++) {
                    meshes[i] = new Mesh();
                    meshes[i].name = string.Format(colliderNameFormat, i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));

                    var solidOffsetStart = solidFaceOffsets[i];
                    var solidOffsetEnd = solidFaceOffsets[i+1];
                    var finalVertCount = faceVertexOffsets[solidOffsetEnd] - faceVertexOffsets[solidOffsetStart];
                    var finalTriCount = faceTriIndexCounts[solidOffsetEnd] - faceTriIndexCounts[solidOffsetStart];

                    var meshData = outputMesh[i];
                    meshData.SetVertexBufferParams(finalVertCount,
                        new VertexAttributeDescriptor(VertexAttribute.Position)
                    );
                    meshData.SetIndexBufferParams(finalTriCount, IndexFormat.UInt32);
                }
                
                var jobData = new ColliderJob();
                jobData.faceVertexOffsets = faceVertexOffsets;
                jobData.faceTriIndexCounts = faceTriIndexCounts;
                jobData.solidFaceOffsets = solidFaceOffsets;

                #if SCOPA_USE_BURST
                jobData.faceVertices = faceVertices.Reinterpret<float3>();
                jobData.facePlaneNormals = facePlaneNormals.Reinterpret<float3>();
                #else
                jobData.faceVertices = faceVertices;
                jobData.facePlaneNormals = facePlaneNormals;
                #endif

                jobData.meshDataArray = outputMesh;
                jobData.canBeBoxColliderResults = canBeBoxCollider;
                jobData.colliderMode = config.colliderMode;
                jobData.scalingFactor = config.scalingFactor;
                jobData.meshOrigin = gameObject.transform.position;
                jobHandle = jobData.Schedule(solidCount, 64);
            }

            public Mesh[] Complete() {
                jobHandle.Complete();

                Mesh.ApplyAndDisposeWritableMeshData(outputMesh, meshes);
                for (int i = 0; i < meshes.Length; i++) {
                    Mesh newMesh = meshes[i];
                    var newGO = new GameObject(newMesh.name);
                    newGO.transform.SetParent( gameObject.transform );
                    newGO.transform.localPosition = Vector3.zero;
                    newGO.transform.localRotation = Quaternion.identity;
                    newGO.transform.localScale = Vector3.one;

                    newMesh.RecalculateBounds();
                    if (canBeBoxCollider[i]) { // if box collider, we'll just use the mesh bounds to config a collider
                        var bounds = newMesh.bounds;
                        var boxCol = newGO.AddComponent<BoxCollider>();
                        boxCol.center = bounds.center;
                        boxCol.size = bounds.size;
                        boxCol.isTrigger = isTrigger;
                    } else { // but usually this is a convex mesh collider
                        var newMeshCollider = newGO.AddComponent<MeshCollider>();
                        newMeshCollider.convex = isTrigger ? true : isConvex;
                        newMeshCollider.isTrigger = isTrigger;
                        newMeshCollider.sharedMesh = newMesh;
                    }
                }

                faceVertexOffsets.Dispose();
                faceTriIndexCounts.Dispose();
                solidFaceOffsets.Dispose();

                faceVertices.Dispose();
                facePlaneNormals.Dispose();
                canBeBoxCollider.Dispose();

                return meshes;
            }

            #if SCOPA_USE_BURST
            [BurstCompile]
            #endif
            public struct ColliderJob : IJobParallelFor
            {
                [ReadOnlyAttribute] public NativeArray<int> faceVertexOffsets, faceTriIndexCounts, solidFaceOffsets; // index = i

                #if SCOPA_USE_BURST
                [ReadOnlyAttribute] public NativeArray<float3> faceVertices, facePlaneNormals;
                [ReadOnlyAttribute] public float3 meshOrigin;
                #else            
                [ReadOnlyAttribute] public NativeArray<Vector3> faceVertices, facePlaneNormals;
                [ReadOnlyAttribute] public Vector3 meshOrigin;
                #endif
                
                public Mesh.MeshDataArray meshDataArray;
                [WriteOnly] public NativeArray<bool> canBeBoxColliderResults;

                [ReadOnlyAttribute] public float scalingFactor;
                [ReadOnlyAttribute] public ScopaMapConfig.ColliderImportMode colliderMode;

                public void Execute(int i)
                {
                    var solidOffsetStart = solidFaceOffsets[i];
                    var solidOffsetEnd = solidFaceOffsets[i+1];

                    var solidVertStart = faceVertexOffsets[solidOffsetStart];
                    var finalVertCount = faceVertexOffsets[solidOffsetEnd] - solidVertStart;
                    var finalTriIndexCount = faceTriIndexCounts[solidOffsetEnd] - faceTriIndexCounts[solidOffsetStart];

                    var meshData = meshDataArray[i];

                    #if SCOPA_USE_BURST
                    var outputVerts = meshData.GetVertexData<float3>();
                    #else
                    var outputVerts = meshData.GetVertexData<Vector3>();
                    #endif

                    var outputTris = meshData.GetIndexData<int>();

                    // for each solid, gather faces...
                    var canBeBoxCollider = colliderMode == ScopaMapConfig.ColliderImportMode.BoxAndConvex || colliderMode == ScopaMapConfig.ColliderImportMode.BoxColliderOnly;
                    for(int face=solidOffsetStart; face<solidOffsetEnd; face++) {
                        // don't bother doing BoxCollider test if we're forcing BoxColliderOnly
                        if (canBeBoxCollider && colliderMode != ScopaMapConfig.ColliderImportMode.BoxColliderOnly && !IsNormalAxisAligned(facePlaneNormals[face]))
                            canBeBoxCollider = false;

                        var vertOffsetStart = faceVertexOffsets[face];
                        var vertOffsetEnd = faceVertexOffsets[face+1];
                        for( int n=vertOffsetStart; n<vertOffsetEnd; n++ ) {
                            outputVerts[n-solidVertStart] = faceVertices[n] * scalingFactor - meshOrigin;
                        }

                        // verts are already in correct order, add as basic fan pattern (since we know it's a convex face)
                        var triIndexStart = faceTriIndexCounts[face] - faceTriIndexCounts[solidOffsetStart];
                        var faceVertStart = vertOffsetStart-solidVertStart;
                        for(int t=2; t<vertOffsetEnd-vertOffsetStart; t++) {
                            outputTris[triIndexStart+(t-2)*3] = faceVertStart;
                            outputTris[triIndexStart+(t-2)*3+1] = faceVertStart + t-1;
                            outputTris[triIndexStart+(t-2)*3+2] = faceVertStart + t;
                        }
                    }

                    canBeBoxColliderResults[i] = canBeBoxCollider;
                    meshData.subMeshCount = 1;
                    meshData.SetSubMesh(
                        0, 
                        new SubMeshDescriptor(0, finalTriIndexCount)
                    );
                }

                #if SCOPA_USE_BURST
                public static bool IsNormalAxisAligned(float3 faceNormal) {
                    var absNormal = math.abs(faceNormal);
                    return !((absNormal.x > 0.01f && absNormal.x < 0.99f) || (absNormal.z > 0.01f && absNormal.z < 0.99f) || (absNormal.y > 0.01f && absNormal.y < 0.99f));
                }
                #else
                public static bool IsNormalAxisAligned(Vector3 faceNormal) {
                    var absNormal = faceNormal.Absolute();
                    if ( absNormal.x > 0.01f && absNormal.x < 0.99f ) {
                        return false;
                    } else if ( absNormal.z > 0.01f && absNormal.z < 0.99f ) {
                        return false;
                    } else if ( absNormal.y > 0.01f && absNormal.y < 0.99f ) {
                        return false;
                    }
                    return true;
                }
                #endif
            }
        }

        /// <summary> given a brush / solid (and optional textureFilter texture name) it generates mesh data for verts / tris / UV list buffers</summary>
        public static void BufferMeshDataFromSolid(Solid solid, ScopaMapConfig mapConfig, ScopaMapConfig.MaterialOverride textureFilter = null, bool includeDiscardedFaces = false) {
            foreach (var face in solid.Faces) {
                if ( face.Vertices == null || face.Vertices.Count == 0) // this shouldn't happen though
                    continue;

                if ( !includeDiscardedFaces && IsFaceCulledDiscard(face) )
                    continue;

                if ( textureFilter != null && textureFilter.textureName.ToLowerInvariant().GetHashCode() != face.TextureName.GetHashCode() )
                    continue;

                BufferScaledMeshFragmentForFace(
                    solid,
                    face, 
                    mapConfig, 
                    verts, 
                    tris, 
                    uvs, 
                    textureFilter?.material?.mainTexture != null ? textureFilter.material.mainTexture.width : mapConfig.defaultTexSize, 
                    textureFilter?.material?.mainTexture != null ? textureFilter.material.mainTexture.height : mapConfig.defaultTexSize,
                    textureFilter != null ? textureFilter.materialConfig : null
                );
            }
        }

        /// <summary> utility function that actually generates the Mesh object </summary>
        public static Mesh BuildMeshFromBuffers(string meshName, ScopaMapConfig config, Vector3 meshOrigin = default(Vector3), float smoothNormalAngle = 0) {
            var mesh = new Mesh();
            mesh.name = meshName;

            if(verts.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            if ( meshOrigin != default(Vector3) ) {
                for(int i=0; i<verts.Count; i++) {
                    verts[i] -= meshOrigin;
                }
            }

            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.SetUVs(0, uvs);

            mesh.RecalculateBounds();

            mesh.RecalculateNormals(UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds); // built-in Unity method provides a base for SmoothNormalsJobs
            if ( smoothNormalAngle > 0.01f) {
                mesh.SmoothNormalsJobs(smoothNormalAngle);
            }

            if ( config.addTangents )
                mesh.RecalculateTangents();
            
            #if UNITY_EDITOR
            if ( config.addLightmapUV2 ) {
                UnwrapParam.SetDefaults( out var unwrap);
                unwrap.packMargin *= 2;
                Unwrapping.GenerateSecondaryUVSet( mesh, unwrap );
            }

            if ( config.meshCompression != ScopaMapConfig.ModelImporterMeshCompression.Off)
                UnityEditor.MeshUtility.SetMeshCompression(mesh, (ModelImporterMeshCompression)config.meshCompression);
            #endif

            mesh.Optimize();

            return mesh;
        }

        /// <summary> build mesh fragment (verts / tris / uvs), usually run for each face of a solid </summary>
        static void BufferScaledMeshFragmentForFace(Solid brush, Face face, ScopaMapConfig mapConfig, List<Vector3> verts, List<int> tris, List<Vector2> uvs, int textureWidth = 128, int textureHeight = 128, ScopaMaterialConfig materialConfig = null) {
            var lastVertIndexOfList = verts.Count;

            faceVerts.Clear();
            faceUVs.Clear();
            faceTris.Clear();

            // add all verts and UVs
            for( int v=0; v<face.Vertices.Count; v++) {
                faceVerts.Add(face.Vertices[v].ToUnity() * mapConfig.scalingFactor);
                
                faceUVs.Add(new Vector2(
                    (Vector3.Dot(face.Vertices[v].ToUnity(), face.UAxis.ToUnity() / face.XScale) + (face.XShift % textureWidth)) / (textureWidth),
                    (Vector3.Dot(face.Vertices[v].ToUnity(), face.VAxis.ToUnity() / -face.YScale) + (-face.YShift % textureHeight)) / (textureHeight)
                ) * mapConfig.globalTexelScale);
            }

            // verts are already in correct order, add as basic fan pattern (since we know it's a convex face)
            for(int i=2; i<face.Vertices.Count; i++) {
                faceTris.Add(lastVertIndexOfList);
                faceTris.Add(lastVertIndexOfList + i - 1);
                faceTris.Add(lastVertIndexOfList + i);
            }

            // user override
            if (materialConfig != null) {
                materialConfig.OnBuildBrushFace(brush, face, mapConfig, faceVerts, faceUVs, faceTris);
            }

            // add back to global mesh buffer
            verts.AddRange(faceVerts);
            uvs.AddRange(faceUVs);
            tris.AddRange(faceTris);
        }

        public static bool IsMeshBufferEmpty() {
            return verts.Count == 0 || tris.Count == 0;
        }

        public static void ClearMeshBuffers()
        {
            verts.Clear();
            tris.Clear();
            uvs.Clear();
        }

        public static void WeldVertices(this Mesh aMesh, float aMaxDelta = 0.1f, float maxAngle = 180f)
        {
            var verts = aMesh.vertices;
            var normals = aMesh.normals;
            var uvs = aMesh.uv;
            List<int> newVerts = new List<int>();
            int[] map = new int[verts.Length];
            // create mapping and filter duplicates.
            for (int i = 0; i < verts.Length; i++)
            {
                var p = verts[i];
                var n = normals[i];
                var uv = uvs[i];
                bool duplicate = false;
                for (int i2 = 0; i2 < newVerts.Count; i2++)
                {
                    int a = newVerts[i2];
                    if (
                        (verts[a] - p).sqrMagnitude <= aMaxDelta // compare position
                        && Vector3.Angle(normals[a], n) <= maxAngle // compare normal
                        // && (uvs[a] - uv).sqrMagnitude <= aMaxDelta // compare first uv coordinate
                        )
                    {
                        map[i] = i2;
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    map[i] = newVerts.Count;
                    newVerts.Add(i);
                }
            }
            // create new vertices
            var verts2 = new Vector3[newVerts.Count];
            var normals2 = new Vector3[newVerts.Count];
            var uvs2 = new Vector2[newVerts.Count];
            for (int i = 0; i < newVerts.Count; i++)
            {
                int a = newVerts[i];
                verts2[i] = verts[a];
                normals2[i] = normals[a];
                uvs2[i] = uvs[a];
            }
            // map the triangle to the new vertices
            var tris = aMesh.triangles;
            for (int i = 0; i < tris.Length; i++)
            {
                tris[i] = map[tris[i]];
            }
            aMesh.Clear();
            aMesh.vertices = verts2;
            aMesh.normals = normals2;
            aMesh.triangles = tris;
            aMesh.uv = uvs2;
        }

        public static void SmoothNormalsJobs(this Mesh aMesh, float weldingAngle = 80, float maxDelta = 0.1f) {
            var meshData = Mesh.AcquireReadOnlyMeshData(aMesh);
            var verts = new NativeArray<Vector3>(meshData[0].vertexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            meshData[0].GetVertices(verts);
            var normals = new NativeArray<Vector3>(meshData[0].vertexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            meshData[0].GetNormals(normals);
            var smoothNormalsResults = new NativeArray<Vector3>(meshData[0].vertexCount, Allocator.TempJob);
            
            var jobData = new SmoothJob();
            jobData.cos = Mathf.Cos(weldingAngle * Mathf.Deg2Rad);
            jobData.maxDelta = maxDelta;

            #if SCOPA_USE_BURST
            jobData.verts = verts.Reinterpret<float3>();
            jobData.normals = normals.Reinterpret<float3>();
            jobData.results = smoothNormalsResults.Reinterpret<float3>();
            #else
            jobData.verts = verts;
            jobData.normals = normals;
            jobData.results = smoothNormalsResults;
            #endif

            var handle = jobData.Schedule(smoothNormalsResults.Length, 8);
            handle.Complete();

            meshData.Dispose(); // must dispose this early, before modifying mesh

            aMesh.SetNormals(smoothNormalsResults);

            verts.Dispose();
            normals.Dispose();
            smoothNormalsResults.Dispose();
        }

        #if SCOPA_USE_BURST
        [BurstCompile]
        #endif
        public struct SmoothJob : IJobParallelFor
        {
            #if SCOPA_USE_BURST
            [ReadOnlyAttribute] public NativeArray<float3> verts, normals;
            public NativeArray<float3> results;
            #else
            [ReadOnlyAttribute] public NativeArray<Vector3> verts, normals;
            public NativeArray<Vector3> results;
            #endif

            public float cos, maxDelta;

            public void Execute(int i)
            {
                var tempResult = normals[i];
                var resultCount = 1;
                
                for(int i2 = 0; i2 < verts.Length; i2++) {
                    #if SCOPA_USE_BURST
                    if ( math.lengthsq(verts[i2] - verts[i] ) <= maxDelta && math.dot(normals[i2], normals[i] ) >= cos ) 
                    #else
                    if ( (verts[i2] - verts[i] ).sqrMagnitude <= maxDelta && Vector3.Dot(normals[i2], normals[i] ) >= cos ) 
                    #endif
                    {
                        tempResult += normals[i2];
                        resultCount++;
                    }
                }

                if (resultCount > 1)
                #if SCOPA_USE_BURST
                    tempResult = math.normalize(tempResult / resultCount);
                #else
                    tempResult = (tempResult / resultCount).normalized;
                #endif
                results[i] = tempResult;
            }
        }

        public static void SnapBrushVertices(Solid sledgeSolid, float snappingDistance = 4f) {
            // snap nearby vertices together within in each solid -- but always snap to the FURTHEST vertex from the center
            var origin = new System.Numerics.Vector3();
            var vertexCount = 0;
            foreach(var face in sledgeSolid.Faces) {
                for(int i=0; i<face.Vertices.Count; i++) {
                    origin += face.Vertices[i];
                }
                vertexCount += face.Vertices.Count;
            }
            origin /= vertexCount;

            foreach(var face1 in sledgeSolid.Faces) {
                foreach (var face2 in sledgeSolid.Faces) {
                    if ( face1 == face2 )
                        continue;

                    for(int a=0; a<face1.Vertices.Count; a++) {
                        for(int b=0; b<face2.Vertices.Count; b++ ) {
                            if ( (face1.Vertices[a] - face2.Vertices[b]).LengthSquared() < snappingDistance * snappingDistance ) {
                                if ( (face1.Vertices[a] - origin).LengthSquared() > (face2.Vertices[b] - origin).LengthSquared() )
                                    face2.Vertices[b] = face1.Vertices[a];
                                else
                                    face1.Vertices[a] = face2.Vertices[b];
                            }
                        }
                    }
                }
            }
        }

    }
}