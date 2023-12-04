using System;
using UnityEditor;
using UnityEngine;

namespace Scopa.Editor.Importers
{
    public class LightEntityPostProcessor : AssetPostprocessor
    {
        void OnPostprocessPrefab(GameObject g)
        {
            Apply(g.transform);
        }

        void Apply(Transform t)
        {
            if (t.TryGetComponent(out ScopaEntity scopaEntity))
            {
                Debug.Log(scopaEntity.name);
            }

        }
        
        // https://stackoverflow.com/questions/444798/case-insensitive-containsstring
        /// <summary>
        /// Case insensitive contains
        /// </summary>
        /// <param name="source"></param>
        /// <param name="str"></param>
        /// <param name="comp"></param>
        /// <returns></returns>
        public bool Contains2(string source, string str)
        {
            var comp = StringComparison.OrdinalIgnoreCase;
            return source?.IndexOf(str, comp) >= 0;
        }
        
        void OnPreprocessAsset()
        {
            ModelImporter modelImporter = assetImporter as ModelImporter;
            if (modelImporter != null)
            {
                // log name
                Debug.Log(modelImporter.assetPath);
                // if (!assetPath.Contains("@"))
                //modelImporter.importAnimation = false;
                // modelImporter.materialImportMode = ModelImporterMaterialImportMode.None;
            }
            
            
            if (assetImporter.importSettingsMissing)
            {
                //ModelImporter modelImporter = assetImporter as ModelImporter;
                //if (modelImporter != null)
                {
                    // log name
                    //Debug.Log(modelImporter.assetPath);
                   // if (!assetPath.Contains("@"))
                        //modelImporter.importAnimation = false;
                   // modelImporter.materialImportMode = ModelImporterMaterialImportMode.None;
                }
            }
        }
    }
}