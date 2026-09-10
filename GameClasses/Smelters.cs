using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using AutomaticFuel;
using UnityEngine;
using System.Reflection;
using System.CodeDom;


namespace AutomaticFuel.GameClasses
{
    internal class Smelters
    {

        private static Dictionary<string, ItemDrop> metals = new Dictionary<string, ItemDrop>
        {
            { "$item_copperore", null },
            { "$item_copper", null },
            { "$item_ironscrap", null },
            { "$item_ironore", null },
            { "$item_iron", null },
            { "$item_tinore", null },
            { "$item_tin", null },
            { "$item_silverore", null },
            { "$item_silver", null },
            { "$item_copperscrap", null },
            { "$item_bronzescrap", null },
             { "$item_bronze", null }
        };

        [HarmonyPatch(typeof(Smelter), nameof(Smelter.Awake))]
        //allows the use of all fuel in the Blast Furnace 
        public class BlastFurnacePatch
        {
            private static void Prefix(ref Smelter __instance)
            {

                if (AutomaticFuel.AutomaticFuelPlugin.configBlastFurnaceTakesAll.Value)
                {

                    if (__instance.m_name != "$piece_blastfurnace")
                    {
                     //   UnityEngine.Debug.Log("Ignored non-blast furnace smelter.");
                        return;
                    }
                  //  UnityEngine.Debug.Log("Found a blast furnace! Applying fix.");

                    ObjectDB instance = ObjectDB.instance;
                    List<ItemDrop> materials = instance.GetAllItems(ItemDrop.ItemData.ItemType.Material, "");
                    
                    if (AutomaticFuelPlugin.nofloorpickup.Value)
                    {
                        foreach (ItemDrop material in materials)
                        {
                            if (metals.Keys.Contains(material.m_itemData.m_shared.m_name))
                            {
                                UnityEngine.Debug.Log("Adding " + material.m_itemData.m_shared.m_name + " to list of materials.");
                                metals[material.m_itemData.m_shared.m_name] = material;
                            }
                        }
                    }

                    List<Smelter.ItemConversion> conversions = new List<Smelter.ItemConversion>()
                        {
                            new Smelter.ItemConversion{ m_from = metals["$item_copperore"], m_to = metals["$item_copper"]},
                            new Smelter.ItemConversion{ m_from = metals["$item_tinore"], m_to = metals["$item_tin"]},
                            new Smelter.ItemConversion{ m_from = metals["$item_ironscrap"], m_to = metals["$item_iron"]},
                            new Smelter.ItemConversion{ m_from = metals["$item_ironore"], m_to = metals["$item_iron"]},
                            new Smelter.ItemConversion{ m_from = metals["$item_silverore"], m_to = metals["$item_silver"]},
                           new Smelter.ItemConversion{ m_from = metals["$item_copperscrap"], m_to = metals["$item_copper"]},
                           new Smelter.ItemConversion{ m_from = metals["$item_bronzescrap"], m_to = metals["$item_bronze"]},
                        };

                    foreach (Smelter.ItemConversion conversion in conversions)
                    {
                        __instance.m_conversion.Add(conversion);
                    }
                }
            }
        }
        
        //allows the stacking of smelters //

        [HarmonyPatch(typeof(Smelter), "UpdateSmoke")]
        private class SmelterUpdateSmoke_Patch
        {
            private static void Postfix(Smelter __instance)
            //checks to see if stack smelters is true and kills off the smoke blocked check
            {
                if (__instance.m_smokeSpawner != null)
                {
                    if (AutomaticFuelPlugin.configStackSmelters.Value)
                    {
                        __instance.m_smokeSpawner.enabled = false;
                        __instance.m_blockedSmoke = false;
                        return;
                    }
                }
            }
        }
       
        
        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        private static class Smelter_FixedUpdate_Patch
        {
            private static void Postfix(Smelter __instance, ZNetView ___m_nview)
            {
                //if (__instance.name.Contains("piece_spinningwheel"))
                //    __instance.m_haveRoof = true;

                if (!Player.m_localPlayer || !AutomaticFuelPlugin.isOn.Value || ___m_nview == null || !___m_nview.IsOwner())
                    return;
                if (__instance.name.Contains("charcoal_kiln") && AutomaticFuelPlugin.turnOffKiln.Value)
                    return;
                if (__instance.name.Contains("piece_spinningwheel") && AutomaticFuelPlugin.turnOffSpinningWheel.Value)
                    return;
                if (__instance.name.Contains("windmill") && AutomaticFuelPlugin.turnOffWindmills.Value)
                    return;
              

                // added to turn off blast furnace and smelter.  03/02/26
               // AutomaticFuelPlugin.AutomaticFuelLogger.LogInfo(__instance.name);

                if (Time.time - AutomaticFuelPlugin.lastFuel < 0.1)
                {
                    AutomaticFuelPlugin.fuelCount++;
                    RefuelSmelter(__instance, ___m_nview, AutomaticFuelPlugin.fuelCount * 33);
                }
                else
                {
                    AutomaticFuelPlugin.fuelCount = 0;
                    AutomaticFuelPlugin.lastFuel = Time.time;
                    RefuelSmelter(__instance, ___m_nview, 0);
                }
            }
        }



        public static async void RefuelSmelter(Smelter __instance, ZNetView ___m_nview, int delay)
        {
            await Task.Delay(delay);
            
          


            

            if (!__instance || !___m_nview || !___m_nview.IsValid() || !AutomaticFuelPlugin.modEnabled.Value)
                return;

            int maxOre = __instance.m_maxOre - Traverse.Create(__instance).Method("GetQueueSize").GetValue<int>();
            int maxFuel = __instance.m_maxFuel - Mathf.CeilToInt(___m_nview.GetZDO().GetFloat("fuel", 0f));

            List<Container> nearbyOreContainers = TastyUtils.GetNearbyContainers
                (__instance.transform.position, AutomaticFuelPlugin.smelterOreRange.Value);

            List<Container> nearbyFuelContainers = TastyUtils.GetNearbyContainers
                (__instance.transform.position, AutomaticFuelPlugin.smelterFuelRange.Value);
           // AutomaticFuel.AutomaticFuelPlugin.AutomaticFuelLogger.LogInfo(__instance.name);

            if (__instance.name.Contains("charcoal_kiln") && AutomaticFuelPlugin.restrictKilnOutput.Value)
            {
                string outputName = __instance.m_conversion[0].m_to.m_itemData.m_shared.m_name;
                int maxOutput = AutomaticFuelPlugin.restrictKilnOutputAmount.Value - Traverse.Create(__instance).Method("GetQueueSize").GetValue<int>();
                foreach (Container c in nearbyOreContainers)
                {
                    List<ItemDrop.ItemData> itemList = new List<ItemDrop.ItemData>();
                    c.GetInventory().GetAllItems(outputName, itemList);

                    foreach (var outputItem in itemList)
                    {
                        if (outputItem != null)
                            maxOutput -= outputItem.m_stack;
                    }
                }
                if (maxOutput < 0)
                    maxOutput = 0;
                if (maxOre > maxOutput)
                    maxOre = maxOutput;
            }

            bool fueled = false;
            bool ored = false;

            Vector3 position = __instance.transform.position + Vector3.up;
            foreach (Collider collider in Physics.OverlapSphere(position, AutomaticFuelPlugin.dropRange.Value, LayerMask.GetMask(new string[] { "item" })))
            {
                
                
                
                if (collider?.attachedRigidbody)
                {
                    ItemDrop item = collider.attachedRigidbody.GetComponent<ItemDrop>();
                    //Debug.Log($"nearby item name: {item.m_itemData.m_dropPrefab.name}");

                    if (item?.GetComponent<ZNetView>()?.IsValid() != true)
                        continue;

                

                    string name = TastyUtils.GetPrefabName(item.gameObject.name);
                    if (AutomaticFuelPlugin.nofloorpickup.Value)
                    {
                        foreach (Smelter.ItemConversion itemConversion in __instance.m_conversion)
                        {
                            if (ored)
                                break;
                            if (item.m_itemData.m_shared.m_name == itemConversion.m_from.m_itemData.m_shared.m_name && maxOre > 0)
                            {
                                if (AutomaticFuelPlugin.oreDisallowTypes.Value.Split(',').Contains(name))
                                {
                                    //Dbgl($"container at {c.transform.position} has {item.m_itemData.m_stack} {item.m_dropPrefab.name} but it's forbidden by config");
                                    continue;
                                }

                                AutomaticFuelPlugin.Dbgl($"auto adding ore {name} from ground");
                               
                                int amount = Mathf.Min(item.m_itemData.m_stack, maxOre);
                                maxOre -= amount;

                                for (int i = 0; i < amount; i++)
                                {
                                    if (item.m_itemData.m_stack <= 1)
                                    {
                                        if (___m_nview.GetZDO() == null)
                                            AutomaticFuelPlugin.Destroy(item.gameObject);
                                        else
                                            ZNetScene.instance.Destroy(item.gameObject);
                                        ___m_nview.InvokeRPC("RPC_AddOre", new object[] { name, false });
                                        if (AutomaticFuelPlugin.distributedFilling.Value)
                                            ored = true;
                                        break;
                                    }

                                    item.m_itemData.m_stack--;
                                    ___m_nview.InvokeRPC("RPC_AddOre", new object[] { name, false });
                                    Traverse.Create(item).Method("Save").GetValue();
                                    if (AutomaticFuelPlugin.distributedFilling.Value)
                                        ored = true;
                                }
                            }
                        }
                    }
                    if (__instance.m_fuelItem && item.m_itemData.m_shared.m_name ==
                        __instance.m_fuelItem.m_itemData.m_shared.m_name && maxFuel > 0 && !fueled)
                    {
                        //added for the dont pick u from floor

                       


                        if (AutomaticFuelPlugin.nofloorpickup.Value)
                        {
                            if (AutomaticFuelPlugin.fuelDisallowTypes.Value.Split(',').Contains(name))
                            {
                                //Dbgl($"ground has {item.m_itemData.m_dropPrefab.name} but it's forbidden by config");
                                continue;
                            }

                            AutomaticFuelPlugin.Dbgl($"auto adding fuel {name} from ground");

                            int amount = Mathf.Min(item.m_itemData.m_stack, maxFuel);
                            maxFuel -= amount;

                            for (int i = 0; i < amount; i++)
                            {
                                if (item.m_itemData.m_stack <= 1)
                                {
                                    if (___m_nview.GetZDO() == null)
                                        AutomaticFuelPlugin.Destroy(item.gameObject);
                                    else
                                        ZNetScene.instance.Destroy(item.gameObject);
                                    ___m_nview.InvokeRPC("RPC_AddFuel", new object[] { });
                                    if (AutomaticFuelPlugin.distributedFilling.Value)
                                        fueled = true;
                                    break;
                                }

                                item.m_itemData.m_stack--;
                                ___m_nview.InvokeRPC("RPC_AddFuel", new object[] { });
                                Traverse.Create(item).Method("Save").GetValue();
                                if (AutomaticFuelPlugin.distributedFilling.Value)
                                {
                                    fueled = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            foreach (Container c in nearbyOreContainers)
            {
               

                foreach (Smelter.ItemConversion itemConversion in __instance.m_conversion)
                {
                    if (ored)
                        break;
                    List<ItemDrop.ItemData> itemList = new List<ItemDrop.ItemData>();
                    c.GetInventory().GetAllItems(itemConversion.m_from.m_itemData.m_shared.m_name, itemList);

                    foreach (var oreItem in itemList)
                    {
                        if (oreItem != null && maxOre > 0 && (!AutomaticFuelPlugin.leaveLastItem.Value || oreItem.m_stack > 1))
                        {
                            if (AutomaticFuelPlugin.oreDisallowTypes.Value.Split(',').Contains(oreItem.m_dropPrefab.name))
                                continue;
                            maxOre--;

                            AutomaticFuelPlugin.Dbgl($"container at {c.transform.position} has {oreItem.m_stack} {oreItem.m_dropPrefab.name}, taking one");

                            ___m_nview.InvokeRPC("RPC_AddOre", new object[] { oreItem.m_dropPrefab?.name, false });
                            c.GetInventory().RemoveItem(itemConversion.m_from.m_itemData.m_shared.m_name, 1);
                            typeof(Container).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(c, new object[] { });
                            typeof(Inventory).GetMethod("Changed", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(c.GetInventory(), new object[] { });
                            if (AutomaticFuelPlugin.distributedFilling.Value)
                            {
                                ored = true;
                                break;
                            }
                        }
                    }
                }
            }
            foreach (Container c in nearbyFuelContainers)
            {
                if (!__instance.m_fuelItem || maxFuel <= 0 || fueled)
                    break;

                List<ItemDrop.ItemData> itemList = new List<ItemDrop.ItemData>();
                c.GetInventory().GetAllItems(__instance.m_fuelItem.m_itemData.m_shared.m_name, itemList);

                foreach (var fuelItem in itemList)
                {
                    if (fuelItem != null && (!AutomaticFuelPlugin.leaveLastItem.Value || fuelItem.m_stack > 1))
                    {
                        maxFuel--;
                        if (AutomaticFuelPlugin.fuelDisallowTypes.Value.Split(',').Contains(fuelItem.m_dropPrefab.name))
                        {
                            //Dbgl($"container at {c.transform.position} has {item.m_stack} {item.m_dropPrefab.name} but it's forbidden by config");
                            continue;
                        }

                        AutomaticFuelPlugin.Dbgl($"container at {c.transform.position} has {fuelItem.m_stack} {fuelItem.m_dropPrefab.name}, taking one");

                        ___m_nview.InvokeRPC("RPC_AddFuel", new object[] { });

                        c.GetInventory().RemoveItem(__instance.m_fuelItem.m_itemData.m_shared.m_name, 1);
                        typeof(Container).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(c, new object[] { });
                        typeof(Inventory).GetMethod("Changed", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(c.GetInventory(), new object[] { });
                        if (AutomaticFuelPlugin.distributedFilling.Value)
                        {
                            fueled = true;
                            break;
                        }
                    }
                }
            }
        }
    }
}
