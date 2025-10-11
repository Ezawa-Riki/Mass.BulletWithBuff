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

        // Static constructor to load configuration file
        static AmmoPatch()
        {
            LoadConfig();
        }

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

                // Check if there's a corresponding buff configuration
                if (_bulletBuffMappings.TryGetValue(damage.SourceId, out string buffName))
                {
                    // Console.WriteLine($"AmmoPatch: Buff ammo detected {damage.SourceId}, preparing to apply {buffName}");
                    ApplyBuffEffect(__instance, damage, bodyPart, buffName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AmmoPatch: Postfix Error - {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void ApplyBuffEffect(ActiveHealthController healthController, DamageInfoStruct damage, EBodyPart bodyPart, string buffName)
        {
            try
            {
                // Validate parameters
                if (healthController == null)
                {
                    Console.WriteLine("ApplyBuffEffect: healthController is null");
                    return;
                }

                // if (damage.Weapon == null)
                // {
                //     Console.WriteLine("ApplyBuffEffect: damage.Weapon is null");
                //     return;
                // }

                // Get Stimulator type (protected nested type)
                Type stimulatorType = typeof(ActiveHealthController).GetNestedType("Stimulator", BindingFlags.NonPublic | BindingFlags.Instance);
                if (stimulatorType == null)
                {
                    Console.WriteLine("ApplyBuffEffect: Can't get Stimulator type");
                    return;
                }
                // Console.WriteLine("ApplyBuffEffect: Get Stimulator type success");

                // Create custom stimulator value object
                var stimValue = new ClassStimulatorValue(buffName, damage.Weapon, bodyPart);

                // Get method_13 and specify generic parameter as Stimulator
                MethodInfo method13 = typeof(ActiveHealthController)
                    .GetMethod("method_13", BindingFlags.Instance | BindingFlags.Public);

                if (method13 == null)
                {
                    Console.WriteLine("ApplyBuffEffect: Can't find method_13 method");
                    return;
                }

                // Apply generic parameter
                MethodInfo genericMethod13 = method13.MakeGenericMethod(stimulatorType);
                if (genericMethod13 == null)
                {
                    Console.WriteLine("ApplyBuffEffect: Can't make generic method for method_13");
                    return;
                }
                // Console.WriteLine("ApplyBuffEffect: Get and prepare method_13 success");

                // Create callback method
                Type actionType = typeof(Action<>).MakeGenericType(stimulatorType);
                MethodInfo method0 = typeof(ClassStimulatorValue).GetMethod("method_0", BindingFlags.Instance | BindingFlags.Public);

                if (method0 == null)
                {
                    Console.WriteLine("ApplyBuffEffect: Can't find method_0 method in ClassStimulatorValue");
                    return;
                }

                Delegate callback;
                try
                {
                    callback = Delegate.CreateDelegate(actionType, stimValue, method0);
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
                    EBodyPart.Head,    // bodyPart parameter
                    damage.Weapon,     // effectSourceItem parameter
                    null,              // strength parameter
                    null,              // delay parameter
                    null,              // duration parameter
                    null,              // residueTime parameter
                    callback           // initCallback parameter
                };

                // Invoke method_13
                try
                {
                    object result = genericMethod13.Invoke(healthController, parameters);
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
                if (weapon == null)
                {
                    // Console.WriteLine("ClassStimulatorValue: weapon is null");

                }
                else
                {
                    weaponId = weapon.TemplateId.ToString();
                    if (string.IsNullOrEmpty(weaponId))
                    {
                        Console.WriteLine("ClassStimulatorValue: Weapon ID is null or empty or conversion failed");
                        return;
                    }
                }
                // Use reflection to find and invoke StoreValues method
                MethodInfo storeValuesMethod = stimulator.GetType().GetMethod("StoreValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);


                object[] args = new object[] { buffName, weaponId, bodyPart };


                // Invoke StoreValues method
                storeValuesMethod.Invoke(stimulator, args);
                // Console.WriteLine("ClassStimulatorValue: StoreValues invoked successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ClassStimulatorValue: method_0 error - {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}