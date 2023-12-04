using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Diagnostics;
using Debug = UnityEngine.Debug;

#if UNITY_2020_2_OR_NEWER
using UnityEditor.AssetImporters;
#else
using UnityEditor.Experimental.AssetImporters;
#endif

namespace Scopa.Editor {

    /// <summary>
    /// custom Unity importer that detects MAP, RMF, VMF, or JMF files in /Assets/
    /// and automatically imports them like any other 3D mesh
    /// </summary>
    [ScriptedImporter(1, new string[] {"map", "rmf", "vmf", "jmf"}, 6900)]
    public class MapImporter : ScriptedImporter
    {
        public ScopaMapConfigAsset externalConfig;
        public ScopaMapConfig config;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var currentConfig = externalConfig != null ? externalConfig.config : config;

            if ( currentConfig == null ) {
                currentConfig = new ScopaMapConfig();
            }

            var filepath = Application.dataPath + ctx.assetPath.Substring("Assets".Length);

            var gameObject = ScopaCore.ImportMap(filepath, currentConfig, out var meshList);
            ctx.AddObjectToAsset(gameObject.name, gameObject);

            // we have to serialize every mesh as a subasset, or else it won't get saved
            foreach ( var meshKVP in meshList ) {
                ctx.AddObjectToAsset(meshKVP.Key.name, meshKVP.Key);
                EditorUtility.SetDirty(meshKVP.Key);
            //    PrefabUtility.RecordPrefabInstancePropertyModifications(mesh);
            }
            ctx.SetMainObject(gameObject);

            EditorUtility.SetDirty(gameObject);
            
            // additional
            PostProcessLights(gameObject);
            EditorUtility.SetDirty(gameObject);
        }

        public void PostProcessLights(GameObject gameObject)
        {
            // get all children
            var children = gameObject.GetComponentsInChildren<Transform>();

            foreach (var child in children)
            {
                // temp disable
                //if (child.gameObject.name.ContainsIgnoreCase("light"))
                {
                    TryAddLight(child.gameObject);
                }
            }
            

        }

        /// <summary>
        /// Additional Light Entity Data Processing
        /// </summary>
        /// <param name="gameObject"></param>
        private void TryAddLight(GameObject gameObject)
        {
            var scopaEntity = gameObject.GetComponent<ScopaEntity>();
            if (scopaEntity == null) 
                return;
            
            // if no entity data, skip
            if(scopaEntity.entityData == null)
                return;
                
            var entityData = scopaEntity.entityData;
            // if not light, skip
            if(!entityData.ClassName.ContainsIgnoreCase("light"))
                return;
            
            // if already has light component, skip
            if (gameObject.GetComponent<Light>() != null)
                return;
            
            var light = gameObject.AddComponent<Light>();
            // Initialize defaults
            light.type    = LightType.Point;
            light.shadows = LightShadows.Soft;
            
            if(entityData.ClassName.ContainsIgnoreCase("realtime"))
                light.lightmapBakeType = LightmapBakeType.Realtime;
            if(entityData.ClassName.ContainsIgnoreCase("baked"))
                light.lightmapBakeType = LightmapBakeType.Mixed;
            if(entityData.ClassName.ContainsIgnoreCase("mixed"))
                light.lightmapBakeType = LightmapBakeType.Mixed;
            
            // iterate through all class properties
            foreach (var dataProperty in scopaEntity.entityData.Properties)
            {
                var key = dataProperty.Key;

                SetLightIntensity(key, dataProperty, gameObject, ref light);
                SetLightColor(key, dataProperty, gameObject, ref light);
                SetLightAngle(key, dataProperty, gameObject, ref light);
            }
        }

        private void SetLightAngle(string key, KeyValuePair<string, string> dataProperty, GameObject gameObject, ref Light light)
        {
            
        }

        private static void SetLightIntensity(string key, KeyValuePair<string, string> dataProperty,
            GameObject gameObject, ref Light light)
        {
            // note 'light' was original quake fgd name for intensity
            if (!key.ContainsIgnoreCase("intensity")) 
                return;
            
            if(int.TryParse(dataProperty.Value, out var intensity))
            {
                light.intensity = intensity;
            }
            else if(float.TryParse(dataProperty.Value, out var floatintensity))
            {
                light.intensity = floatintensity;
            }
        }
        private static void SetLightColor(string key,        KeyValuePair<string, string> dataProperty,
            GameObject                               gameObject, ref Light                    light)
        {
            if (!key.ContainsIgnoreCase("color")) 
                return;
            
            
            
            // parse a string where values are separated by spaces
            var stringRGBDataArray = dataProperty.Value.Split(' ');
            if (stringRGBDataArray.Length != 3)
            {
                Debug.LogError($"Could not parse {dataProperty.Value} to color, expected 3 values separated by spaces but got {stringRGBDataArray.Length}");
                return;
            }
            var colorValues = new float[stringRGBDataArray.Length];
            for (var index = 0; index < colorValues.Length; index++)
            {
                var stringRGBData = stringRGBDataArray[index];
                colorValues[index] = ParseInteger(stringRGBData);
            }
            
            
            var color = ConvertToUnityColor((int)colorValues[0], (int)colorValues[1], (int)colorValues[2]);
            light.color = color;
        }
        
        
        private static float ParseInteger(string value)
        {
            var returnValue = 0f;
            if(int.TryParse(value, out var intResult))
            {
                returnValue = intResult;
                return returnValue;
            }
            else if(float.TryParse(value, out var floatResult))
            {
                returnValue = floatResult;
                return returnValue;
            }
            Debug.LogError($"Could not parse {value} to integer or float");
            return returnValue;
        }
        
        // Method to convert RGB values from 0-255 to 0-1 range
        public static Color ConvertToUnityColor(int red, int green, int blue)
        {
            // Ensure values are within the valid range
            red   = Mathf.Clamp(red, 0, 255);
            green = Mathf.Clamp(green, 0, 255);
            blue  = Mathf.Clamp(blue, 0, 255);

            // Convert values to the 0-1 range
            float convertedRed   = red / 255f;
            float convertedGreen = green / 255f;
            float convertedBlue  = blue / 255f;

            // Create and return the Unity Color
            return new Color(convertedRed, convertedGreen, convertedBlue, 1);
        }
        
        private static void SetLightBakingType(string key,        KeyValuePair<string, string> dataProperty,
            GameObject                               gameObject, ref Light                    light)
        {
            if (!key.ContainsIgnoreCase("type")) 
                return;
            
            var value = dataProperty.Value;
            switch (value)
            {
                case "realtime":
                    light.lightmapBakeType = LightmapBakeType.Realtime;
                    break;
                case "baked":
                    light.lightmapBakeType = LightmapBakeType.Baked;
                    break;
                case "mixed":
                    light.lightmapBakeType = LightmapBakeType.Mixed;
                    break;
                // also support numbers
                case "0":
                    light.lightmapBakeType = LightmapBakeType.Realtime;
                    break;
                case "1":
                    light.lightmapBakeType = LightmapBakeType.Baked;
                    break;
                case "2":
                    light.lightmapBakeType = LightmapBakeType.Mixed;
                    break;
            }
        }
    }

}