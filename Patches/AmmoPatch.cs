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
    // Configuration file data model
    public class BulletBuffConfig
    {
        public Dictionary<string, string> BulletBuffMappings { get; set; } = new Dictionary<string, string>();
    }

    internal class AmmoPatch : ModulePatch
    {
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
                Console.WriteLine("AmmoPatch: Failed to get Stimulator type");
            }

            _method13 = typeof(ActiveHealthController).GetMethod("method_14", BindingFlags.Instance | BindingFlags.Public);
            if (_method13 == null)
            {
                Console.WriteLine("AmmoPatch: Failed to get method_14");
            }
            else if (_stimulatorType != null)
            {
                _genericMethod13 = _method13.MakeGenericMethod(_stimulatorType);
                if (_genericMethod13 == null)
                {
                    Console.WriteLine("AmmoPatch: Failed to make generic method for method_14");
                }
            }

            _method0 = typeof(ClassStimulatorValue).GetMethod("method_0", BindingFlags.Instance | BindingFlags.Public);
            if (_method0 == null)
            {
                Console.WriteLine("AmmoPatch: Failed to get ClassStimulatorValue.method_0");
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
                    Console.WriteLine($"AmmoPatch: Configuration file does not exist - {configPath}");
                    return;
                }

                // Read and parse configuration file
                string jsonContent = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<BulletBuffConfig>(jsonContent);

                if (config?.BulletBuffMappings != null)
                {
                    _bulletBuffMappings = config.BulletBuffMappings;
                    Console.WriteLine($"AmmoPatch: Configuration loaded successfully, {_bulletBuffMappings.Count} mappings loaded");

                    // Output loaded mappings for debugging
                    foreach (var mapping in _bulletBuffMappings)
                    {
                        Console.WriteLine($"AmmoPatch: Ammo ID {mapping.Key} -> Buff {mapping.Value}");
                    }
                }
                else
                {
                    Console.WriteLine("AmmoPatch: Configuration file is empty or invalid");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AmmoPatch: Failed to load configuration file - {ex.Message}\n{ex.StackTrace}");
            }
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
                    if (buffName == "null")
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
                    if (buffNameFromItemClass != "null")
                    {
                        // Cache the result for future use
                        _bulletBuffMappings[damage.SourceId] = buffNameFromItemClass;
                        ApplyBuffEffect(__instance, damage, bodyPart, buffNameFromItemClass);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AmmoPatch: Postfix Error - {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static string GetBuffNameFromAmmoItemClass(string AmmoId)
        {
            _itemFactoryClass ??= Singleton<ItemFactoryClass>.Instance;
            _itemTemplates ??= _itemFactoryClass.ItemTemplates;
            string buffName = "null";
            if (_itemTemplates.TryGetValue(AmmoId, out ItemTemplate AmmoItemTemplate))
            {
                string description = AmmoItemTemplate.Description;
                if (description.StartsWith("Buffs_"))
                {
                    buffName = description;

                    Console.WriteLine($"GetBuffNameFromAmmoItemClass: Found buff name {buffName} for ammo ID {AmmoId}");
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
                    Console.WriteLine("ApplyBuffEffect: healthController is null");
                    return;
                }

                if (_stimulatorType == null || _genericMethod13 == null || _method0 == null || _actionType == null)
                {
                    Console.WriteLine("ApplyBuffEffect: Reflection cache incomplete, aborting");
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
                    Console.WriteLine($"ApplyBuffEffect: Create delegate failed - {ex.Message}");
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
                    Console.WriteLine($"ApplyBuffEffect: method_13 Invocation exception - {ex.InnerException?.Message}\n{ex.InnerException?.StackTrace}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ApplyBuffEffect: method_13 Invocation exception - {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ApplyBuffEffect: Error - {ex.Message}\n{ex.StackTrace}");
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
                    Console.WriteLine("ClassStimulatorValue: Failed to get StoreValues method");
                }
            }
            else
            {
                Console.WriteLine("ClassStimulatorValue: Failed to get Stimulator type");
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
                    Console.WriteLine("ClassStimulatorValue: stimulator is null");
                    return;
                }

                string weaponId = null;
                // Validate if weapon is valid
                if (weapon != null)
                {
                    weaponId = weapon.TemplateId.ToString();
                    if (string.IsNullOrEmpty(weaponId))
                    {
                        Console.WriteLine("ClassStimulatorValue: Weapon ID is null or empty");
                        return;
                    }
                }

                if (_storeValuesMethod == null)
                {
                    Console.WriteLine("ClassStimulatorValue: StoreValues method not cached");
                    return;
                }
                object[] args = new object[] { buffName, weaponId, bodyPart };


                // Invoke StoreValues method
                _storeValuesMethod.Invoke(stimulator, args);
                // Console.WriteLine("ClassStimulatorValue: StoreValues invoked successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ClassStimulatorValue: method_0 error - {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}