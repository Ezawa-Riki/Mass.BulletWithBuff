using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Comfort.Common;

namespace Mass.BulletWithBuff.AmmoPatches
{
    internal static class BulletBuffLog
    {
        public static void Info(string message)
        {
            Mass.BulletWithBuff.Plugin.LogSource?.LogInfo(message);
        }

        public static void Warning(string message)
        {
            Mass.BulletWithBuff.Plugin.LogSource?.LogWarning(message);
        }

        public static void Error(string message)
        {
            Mass.BulletWithBuff.Plugin.LogSource?.LogError(message);
        }
    }

    // Configuration file data model
    public class BulletBuffConfig
    {
        public Dictionary<string, string> BulletBuffMappings { get; set; } = new Dictionary<string, string>();
    }

    internal class AmmoPatch : ModulePatch
    {
        private const string NoBuffValue = "null";

        // Stores mappings from bullet IDs to buff names
        private static Dictionary<string, string> _bulletBuffMappings = new Dictionary<string, string>();

        // Reflection fields for accessing private members
        private static readonly Type _stimulatorType;
        private static readonly MethodInfo _method13;
        private static readonly MethodInfo _genericMethod13;
        private static readonly MethodInfo _method0;  // ClassStimulatorValue.method_0
        private static readonly Type _actionType;     // Action<Stimulator>

        // Static constructor to load configuration file
        static AmmoPatch()
        {
             LoadConfig();

            _stimulatorType = typeof(ActiveHealthController).GetNestedType("Stimulator", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_stimulatorType == null)
            {
                BulletBuffLog.Error("AmmoPatch: Failed to get Stimulator type");
            }

            _method13 = typeof(ActiveHealthController).GetMethod("method_14", BindingFlags.Instance | BindingFlags.Public);
            if (_method13 == null)
            {
                BulletBuffLog.Error("AmmoPatch: Failed to get method_14");
            }
            else if (_stimulatorType != null)
            {
                _genericMethod13 = _method13.MakeGenericMethod(_stimulatorType);
                if (_genericMethod13 == null)
                {
                    BulletBuffLog.Error("AmmoPatch: Failed to make generic method for method_14");
                }
            }

            _method0 = typeof(ClassStimulatorValue).GetMethod("method_0", BindingFlags.Instance | BindingFlags.Public);
            if (_method0 == null)
            {
                BulletBuffLog.Error("AmmoPatch: Failed to get ClassStimulatorValue.method_0");
            }

            if (_stimulatorType != null)
            {
                _actionType = typeof(Action<>).MakeGenericType(_stimulatorType);
            }
        }

        static private ItemFactoryClass _itemFactoryClass;
        static private GClass1408 _itemTemplates;

        // Load configuration file
        private static void LoadConfig()
        {
            try
            {
                // Get configuration file path (same directory as the assembly)
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                string directory = Path.GetDirectoryName(assemblyPath);
                string configPath = Path.Combine(directory, "BulletBuffConfig.json");

                if (!File.Exists(configPath))
                {
                    BulletBuffLog.Warning($"AmmoPatch: Configuration file does not exist - {configPath}");
                    return;
                }

                // Read and parse configuration file
                string jsonContent = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<BulletBuffConfig>(jsonContent);

                if (config?.BulletBuffMappings != null)
                {
                    _bulletBuffMappings = SanitizeMappings(config.BulletBuffMappings);
                    BulletBuffLog.Info($"AmmoPatch: Configuration loaded successfully, {_bulletBuffMappings.Count} mappings loaded");

                    // Output loaded mappings for debugging
                    foreach (var mapping in _bulletBuffMappings)
                    {
                        BulletBuffLog.Info($"AmmoPatch: Ammo ID {mapping.Key} -> Buff {mapping.Value}");
                    }
                }
                else
                {
                    BulletBuffLog.Warning("AmmoPatch: Configuration file is empty or invalid");
                }
            }
            catch (Exception ex)
            {
                BulletBuffLog.Error($"AmmoPatch: Failed to load configuration file - {ex}");
            }
        }

        private static Dictionary<string, string> SanitizeMappings(Dictionary<string, string> rawMappings)
        {
            var sanitizedMappings = new Dictionary<string, string>();
            foreach (var mapping in rawMappings)
            {
                string ammoId = mapping.Key?.Trim();
                string buffName = mapping.Value?.Trim();

                if (string.IsNullOrEmpty(ammoId))
                {
                    BulletBuffLog.Warning("AmmoPatch: Skipping config entry with empty ammo ID");
                    continue;
                }

                if (string.IsNullOrEmpty(buffName))
                {
                    BulletBuffLog.Warning($"AmmoPatch: Empty buff configured for ammo ID {ammoId}, treating as no buff");
                    sanitizedMappings[ammoId] = NoBuffValue;
                    continue;
                }

                if (string.Equals(buffName, NoBuffValue, StringComparison.OrdinalIgnoreCase))
                {
                    sanitizedMappings[ammoId] = NoBuffValue;
                    continue;
                }

                if (!buffName.StartsWith("Buffs_", StringComparison.Ordinal))
                {
                    BulletBuffLog.Warning($"AmmoPatch: Buff '{buffName}' for ammo ID {ammoId} does not start with 'Buffs_'");
                }

                sanitizedMappings[ammoId] = buffName;
            }

            return sanitizedMappings;
        }

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.TryApplySideEffects));
        }

        [PatchPostfix]
        static void Postfix(ActiveHealthController __instance, DamageInfoStruct damage, EBodyPart bodyPart, bool __result)
        {
            try
            {
                // If the original method already returned true, effects have been applied, no need to process further
                if (__result)
                {
                    return;
                }

                if (string.IsNullOrEmpty(damage.SourceId))
                {
                    // Console.WriteLine($"AmmoPatch: SourceId is null or empty, skipping buff processing");
                    return;
                }
                // Check if there's a corresponding buff configuration
                if (_bulletBuffMappings.TryGetValue(damage.SourceId, out string buffName))
                {
                    // Console.WriteLine($"AmmoPatch: Buff ammo detected {damage.SourceId}, preparing to apply {buffName}");
                    if (buffName == NoBuffValue)
                    {
                        // Console.WriteLine($"AmmoPatch: Buff name is 'null', skipping buff application for {damage.SourceId}");
                        return;
                    }
                    ApplyBuffEffect(__instance, damage, bodyPart, buffName);
                }
                else
                {
                    // Console.WriteLine($"BuffNotFound: {damage.SourceId}");
                    string buffNameFromItemClass = GetBuffNameFromAmmoItemClass(damage.SourceId);
                    // Cache both positive and negative lookups to avoid repeated template checks on common ammo.
                    _bulletBuffMappings[damage.SourceId] = buffNameFromItemClass;
                    if (buffNameFromItemClass != NoBuffValue)
                    {
                        ApplyBuffEffect(__instance, damage, bodyPart, buffNameFromItemClass);
                    }
                }
            }
            catch (Exception ex)
            {
                BulletBuffLog.Error($"AmmoPatch: Postfix Error - {ex}");
            }
        }

        private static string GetBuffNameFromAmmoItemClass(string AmmoId)
        {
            _itemFactoryClass ??= Singleton<ItemFactoryClass>.Instance;
            _itemTemplates ??= _itemFactoryClass.ItemTemplates;
            string buffName = NoBuffValue;
            if (_itemTemplates.TryGetValue(AmmoId, out ItemTemplate AmmoItemTemplate))
            {
                string description = AmmoItemTemplate.Description;
                if (!string.IsNullOrEmpty(description) && description.StartsWith("Buffs_", StringComparison.Ordinal))
                {
                    buffName = description;

                    BulletBuffLog.Info($"GetBuffNameFromAmmoItemClass: Found buff name {buffName} for ammo ID {AmmoId}");
                }
            }
            return buffName;
        }

        private static void ApplyBuffEffect(ActiveHealthController healthController, DamageInfoStruct damage, EBodyPart bodyPart, string buffName)
        {
            // Console.WriteLine($"ApplyBuffEffect: Applying {buffName}");
            try
            {
                // Validate parameters
                if (healthController == null)
                {
                    BulletBuffLog.Warning("ApplyBuffEffect: healthController is null");
                    return;
                }

                if (_stimulatorType == null || _genericMethod13 == null || _method0 == null || _actionType == null)
                {
                    BulletBuffLog.Error("ApplyBuffEffect: Reflection cache incomplete, aborting");
                    return;
                }

                // Create custom stimulator value object
                var stimValue = new ClassStimulatorValue(buffName, damage.Weapon, bodyPart);


                Delegate callback;
                try
                {
                    callback = Delegate.CreateDelegate(_actionType, stimValue, _method0);
                }
                catch (Exception ex)
                {
                    BulletBuffLog.Error($"ApplyBuffEffect: Create delegate failed - {ex.Message}");
                    return;
                }
                // Console.WriteLine("ApplyBuffEffect: Create callback delegate success");

                // Prepare parameters for method_13
                object[] parameters = new object[]
                {
                    EBodyPart.Head,
                    damage.Weapon,
                    null,
                    null,
                    null,
                    null,
                    callback
                };

                // Invoke method_13
                try
                {
                    _genericMethod13.Invoke(healthController, parameters);
                }
                catch (TargetInvocationException ex)
                {
                    // Unwrap inner exception
                    BulletBuffLog.Error($"ApplyBuffEffect: method_13 Invocation exception - {ex.InnerException}");
                }
                catch (Exception ex)
                {
                    BulletBuffLog.Error($"ApplyBuffEffect: method_13 Invocation exception - {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                BulletBuffLog.Error($"ApplyBuffEffect: Error - {ex}");
            }
        }
    }

    public class ClassStimulatorValue
    {
        public string buffName;
        public Item weapon;
        public EBodyPart bodyPart;
        private static readonly MethodInfo _storeValuesMethod;

        static ClassStimulatorValue()
        {
            Type stimulatorType = typeof(ActiveHealthController).GetNestedType("Stimulator", BindingFlags.NonPublic | BindingFlags.Instance);
            if (stimulatorType != null)
            {
                _storeValuesMethod = stimulatorType.GetMethod("StoreValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_storeValuesMethod == null)
                {
                    BulletBuffLog.Error("ClassStimulatorValue: Failed to get StoreValues method");
                }
            }
            else
            {
                BulletBuffLog.Error("ClassStimulatorValue: Failed to get Stimulator type");
            }
        }

        public ClassStimulatorValue(string buffName, Item weapon, EBodyPart bodyPart)
        {
            this.buffName = buffName;
            this.weapon = weapon;
            this.bodyPart = bodyPart;
            // Console.WriteLine($"ClassStimulatorValue: Initializing - Buff: {buffName}, Weapon: {weapon?.TemplateId}, BodyPart: {bodyPart}");
        }

        public void method_0(object stimulator)
        {
            try
            {
                // Console.WriteLine("ClassStimulatorValue: method_0 called");

                if (stimulator == null)
                {
                    BulletBuffLog.Warning("ClassStimulatorValue: stimulator is null");
                    return;
                }

                string weaponId = null;
                // Validate if weapon is valid
                if (weapon != null)
                {
                    weaponId = weapon.TemplateId.ToString();
                    if (string.IsNullOrEmpty(weaponId))
                    {
                        BulletBuffLog.Warning("ClassStimulatorValue: Weapon ID is null or empty");
                        return;
                    }
                }

                if (_storeValuesMethod == null)
                {
                    BulletBuffLog.Error("ClassStimulatorValue: StoreValues method not cached");
                    return;
                }
                object[] args = new object[] { buffName, weaponId, bodyPart };


                // Invoke StoreValues method
                _storeValuesMethod.Invoke(stimulator, args);
                // Console.WriteLine("ClassStimulatorValue: StoreValues invoked successfully");
            }
            catch (Exception ex)
            {
                BulletBuffLog.Error($"ClassStimulatorValue: method_0 error - {ex}");
            }
        }
    }
}
