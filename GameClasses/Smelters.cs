using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace AutomaticFuel.GameClasses
{
    internal class Smelters
    {
        // ------------------------------------------------------------------
        //  Blast furnace: let every metal act as "ore"
        // ------------------------------------------------------------------
        private static readonly Dictionary<string, ItemDrop> metals = new Dictionary<string, ItemDrop>
        {
            { "$item_copperore", null },  { "$item_copper", null },
            { "$item_ironscrap", null },  { "$item_ironore", null },  { "$item_iron", null },
            { "$item_tinore", null },     { "$item_tin", null },
            { "$item_silverore", null },  { "$item_silver", null },
            { "$item_copperscrap", null },{ "$item_bronzescrap", null }, { "$item_bronze", null }
        };

        [HarmonyPatch(typeof(Smelter), nameof(Smelter.Awake))]
        public class BlastFurnacePatch
        {
            private static void Prefix(Smelter __instance)
            {
                if (!AutomaticFuelPlugin.configBlastFurnaceTakesAll.Value) return;
                if (__instance == null || __instance.m_name != "$piece_blastfurnace") return;

                ObjectDB db = ObjectDB.instance;
                if (db == null) return;

                foreach (ItemDrop material in db.GetAllItems(ItemDrop.ItemData.ItemType.Material, ""))
                {
                    if (material == null) continue;
                    string id = material.m_itemData.m_shared.m_name;
                    if (metals.ContainsKey(id)) metals[id] = material;
                }

                AddConversion(__instance, "$item_copperore", "$item_copper");
                AddConversion(__instance, "$item_tinore", "$item_tin");
                AddConversion(__instance, "$item_ironscrap", "$item_iron");
                AddConversion(__instance, "$item_ironore", "$item_iron");
                AddConversion(__instance, "$item_silverore", "$item_silver");
                AddConversion(__instance, "$item_copperscrap", "$item_copper");
                AddConversion(__instance, "$item_bronzescrap", "$item_bronze");
            }

            private static void AddConversion(Smelter smelter, string from, string to)
            {
                ItemDrop fromDrop = metals[from];
                ItemDrop toDrop = metals[to];

                // A conversion with a null m_from NREs later in RefuelSmelter when
                // m_from.m_itemData is dereferenced. Skip rather than push a broken entry.
                if (fromDrop == null || toDrop == null)
                {
                    AutomaticFuelPlugin.AutomaticFuelLogger.LogWarning(
                        $"[AutomaticFuel] blast furnace conversion {from} -> {to} skipped (item not in ObjectDB)");
                    return;
                }

                smelter.m_conversion.Add(new Smelter.ItemConversion { m_from = fromDrop, m_to = toDrop });
            }
        }

        // ------------------------------------------------------------------
        //  Stacked smelters: defeat the "blocked by smoke" check
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(Smelter), "UpdateSmoke")]
        private class SmelterUpdateSmoke_Patch
        {
            private static void Postfix(Smelter __instance)
            {
                if (!AutomaticFuelPlugin.configStackSmelters.Value) return;
                if (__instance == null || __instance.m_smokeSpawner == null) return;

                __instance.m_smokeSpawner.enabled = false;
                __instance.m_blockedSmoke = false;
            }
        }

        // ------------------------------------------------------------------
        //  Fuel loop
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        private static class Smelter_FixedUpdate_Patch
        {
            private static void Postfix(Smelter __instance, ZNetView ___m_nview)
            {
                if (!__instance || !Player.m_localPlayer || !AutomaticFuelPlugin.isOn.Value) return;
                if (___m_nview == null || !___m_nview.IsOwner()) return;
                if (!AutomaticFuelPlugin.modEnabled.Value) return;

                if (__instance.name.Contains("charcoal_kiln") && AutomaticFuelPlugin.turnOffKiln.Value) return;
                if (__instance.name.Contains("piece_spinningwheel") && AutomaticFuelPlugin.turnOffSpinningWheel.Value) return;
                if (__instance.name.Contains("windmill") && AutomaticFuelPlugin.turnOffWindmills.Value) return;

                if (Time.time - AutomaticFuelPlugin.lastFuel < 0.1f)
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

            /// <summary>
            /// Matches on the GameObject name OR m_name. The old "$piece_smelter" / "$piece_blastfurnace"
            /// comparisons could never fire: a GameObject name carries no '$' prefix.
            /// </summary>
            internal static bool MatchesKind(Smelter smelter, string token)
            {
                if (!smelter) return false;

                if (!string.IsNullOrEmpty(smelter.name) &&
                    smelter.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;

                return !string.IsNullOrEmpty(smelter.m_name) &&
                       smelter.m_name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        // ------------------------------------------------------------------
        //  The work
        // ------------------------------------------------------------------
        public static async void RefuelSmelter(Smelter __instance, ZNetView ___m_nview, int delay)
        {
            // The await is INSIDE the try on purpose: an exception escaping an async void is
            // re-thrown on Unity's SynchronizationContext, which is why your log has no
            // "at ...:line N". Catching here restores the real stack.
            try
            {
                await Task.Delay(delay);

                if (!__instance || !___m_nview || !___m_nview.IsValid()) return;
                if (!AutomaticFuelPlugin.modEnabled.Value) return;

                ZDO zdo = ___m_nview.GetZDO();
                if (zdo == null) return;

                int maxOre = __instance.m_maxOre - NonPublicCalls.QueueSize(__instance);
                int maxFuel = __instance.m_maxFuel - Mathf.CeilToInt(zdo.GetFloat("fuel", 0f));

                // Hoisted: these were re-split for every single item examined.
                string[] oreDisallowed = AutomaticFuelPlugin.oreDisallowTypes.Value.Split(',');
                string[] fuelDisallowed = AutomaticFuelPlugin.fuelDisallowTypes.Value.Split(',');

                List<Container> nearbyOreContainers = TastyUtils.GetNearbyContainers(
                    __instance.transform.position, AutomaticFuelPlugin.smelterOreRange.Value);

                List<Container> nearbyFuelContainers = TastyUtils.GetNearbyContainers(
                    __instance.transform.position, AutomaticFuelPlugin.smelterFuelRange.Value);

                if (__instance.name.Contains("charcoal_kiln") && AutomaticFuelPlugin.restrictKilnOutput.Value)
                {
                    string outputName = __instance.m_conversion[0].m_to.m_itemData.m_shared.m_name;
                    int maxOutput = AutomaticFuelPlugin.restrictKilnOutputAmount.Value
                                    - NonPublicCalls.QueueSize(__instance);

                    foreach (Container c in nearbyOreContainers)
                    {
                        List<ItemDrop.ItemData> itemList = new List<ItemDrop.ItemData>();
                        c.GetInventory().GetAllItems(outputName, itemList);

                        foreach (ItemDrop.ItemData outputItem in itemList)
                            if (outputItem != null) maxOutput -= outputItem.m_stack;
                    }

                    maxOre = Mathf.Min(maxOre, Mathf.Max(maxOutput, 0));
                }

                bool fueled = false;
                bool ored = false;

                // ----------------------------------------------------------
                //  Loose items on the ground
                // ----------------------------------------------------------
                if (AutomaticFuelPlugin.nofloorpickup.Value)
                {
                    Vector3 position = __instance.transform.position + Vector3.up;

                    foreach (Collider collider in Physics.OverlapSphere(
                                 position, AutomaticFuelPlugin.dropRange.Value, LayerMask.GetMask("item")))
                    {
                        if (collider == null || collider.attachedRigidbody == null) continue;

                        ItemDrop item = collider.attachedRigidbody.GetComponent<ItemDrop>();
                        if (item == null || item.GetComponent<ZNetView>()?.IsValid() != true) continue;

                        string name = TastyUtils.GetPrefabName(item.gameObject.name);

                        // ---- ore on the ground ----
                        foreach (Smelter.ItemConversion conversion in __instance.m_conversion)
                        {
                            if (ored) break;
                            if (conversion.m_from == null) continue;

                            if (item.m_itemData.m_shared.m_name != conversion.m_from.m_itemData.m_shared.m_name
                                || maxOre <= 0) continue;

                            if (oreDisallowed.Contains(name)) continue;

                            AutomaticFuelPlugin.Dbgl($"auto adding ore {name} from ground");

                            int amount = Mathf.Min(item.m_itemData.m_stack, maxOre);
                            maxOre -= amount;

                            for (int i = 0; i < amount; i++)
                            {
                                if (item.m_itemData.m_stack <= 1)
                                {
                                    DestroyItem(___m_nview, item);
                                    ___m_nview.InvokeRPC("RPC_AddOre", new object[] { name, false });
                                    if (AutomaticFuelPlugin.distributedFilling.Value) ored = true;
                                    break;
                                }

                                item.m_itemData.m_stack--;
                                ___m_nview.InvokeRPC("RPC_AddOre", new object[] { name, false });
                                NonPublicCalls.SaveItemDrop(item);
                                if (AutomaticFuelPlugin.distributedFilling.Value) ored = true;
                            }
                        }

                        // ---- fuel on the ground ----
                        if (__instance.m_fuelItem != null && maxFuel > 0 && !fueled &&
                            item.m_itemData.m_shared.m_name == __instance.m_fuelItem.m_itemData.m_shared.m_name)
                        {
                            if (fuelDisallowed.Contains(name)) continue;

                            AutomaticFuelPlugin.Dbgl($"auto adding fuel {name} from ground");

                            int amount = Mathf.Min(item.m_itemData.m_stack, maxFuel);
                            maxFuel -= amount;

                            for (int i = 0; i < amount; i++)
                            {
                                if (item.m_itemData.m_stack <= 1)
                                {
                                    DestroyItem(___m_nview, item);
                                    ___m_nview.InvokeRPC("RPC_AddFuel", new object[] { });
                                    if (AutomaticFuelPlugin.distributedFilling.Value) fueled = true;
                                    break;
                                }

                                item.m_itemData.m_stack--;
                                ___m_nview.InvokeRPC("RPC_AddFuel", new object[] { });
                                NonPublicCalls.SaveItemDrop(item);
                                if (AutomaticFuelPlugin.distributedFilling.Value)
                                {
                                    fueled = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                // ----------------------------------------------------------
                //  Ore from containers   <-- original crash site
                // ----------------------------------------------------------
                foreach (Container c in nearbyOreContainers)
                {
                    foreach (Smelter.ItemConversion conversion in __instance.m_conversion)
                    {
                        if (ored) break;
                        if (conversion.m_from == null) continue;

                        List<ItemDrop.ItemData> itemList = new List<ItemDrop.ItemData>();
                        c.GetInventory().GetAllItems(conversion.m_from.m_itemData.m_shared.m_name, itemList);

                        foreach (ItemDrop.ItemData oreItem in itemList)
                        {
                            if (oreItem == null || maxOre <= 0) continue;
                            if (AutomaticFuelPlugin.leaveLastItem.Value && oreItem.m_stack <= 1) continue;

                            if (oreItem.m_dropPrefab == null) continue;
                            if (oreDisallowed.Contains(oreItem.m_dropPrefab.name)) continue;

                            maxOre--;

                            AutomaticFuelPlugin.Dbgl(
                                $"container at {c.transform.position} has {oreItem.m_stack} " +
                                $"{oreItem.m_dropPrefab.name}, taking one");

                            ___m_nview.InvokeRPC("RPC_AddOre", new object[] { oreItem.m_dropPrefab.name, false });
                            c.GetInventory().RemoveItem(conversion.m_from.m_itemData.m_shared.m_name, 1);

                            // Was: typeof(Container).GetMethod("Save", NonPublic | Instance)
                            //          .Invoke(c, new object[] { });
                            //      typeof(Inventory).GetMethod("Changed", NonPublic | Instance)
                            //          .Invoke(c.GetInventory(), new object[] { });
                            // Both hard-coded an empty argument array -> TargetParameterCountException
                            // once 1.0 changed the parameter list.
                            NonPublicCalls.PersistContainer(c);

                            if (AutomaticFuelPlugin.distributedFilling.Value)
                            {
                                ored = true;
                                break;
                            }
                        }
                    }
                }

                // ----------------------------------------------------------
                //  Fuel from containers   <-- and the second crash site
                // ----------------------------------------------------------
                foreach (Container c in nearbyFuelContainers)
                {
                    if (__instance.m_fuelItem == null || maxFuel <= 0 || fueled) break;

                    List<ItemDrop.ItemData> itemList = new List<ItemDrop.ItemData>();
                    c.GetInventory().GetAllItems(__instance.m_fuelItem.m_itemData.m_shared.m_name, itemList);

                    foreach (ItemDrop.ItemData fuelItem in itemList)
                    {
                        if (fuelItem == null || maxFuel <= 0) continue;
                        if (AutomaticFuelPlugin.leaveLastItem.Value && fuelItem.m_stack <= 1) continue;

                        if (fuelItem.m_dropPrefab == null) continue;
                        if (fuelDisallowed.Contains(fuelItem.m_dropPrefab.name)) continue;

                        maxFuel--;

                        AutomaticFuelPlugin.Dbgl(
                            $"container at {c.transform.position} has {fuelItem.m_stack} " +
                            $"{fuelItem.m_dropPrefab.name}, taking one");

                        ___m_nview.InvokeRPC("RPC_AddFuel", new object[] { });
                        c.GetInventory().RemoveItem(__instance.m_fuelItem.m_itemData.m_shared.m_name, 1);
                        NonPublicCalls.PersistContainer(c);

                        if (AutomaticFuelPlugin.distributedFilling.Value)
                        {
                            fueled = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                AutomaticFuelPlugin.AutomaticFuelLogger.LogError($"[AutomaticFuel] RefuelSmelter failed: {e}");
            }
        }

        private static void DestroyItem(ZNetView nview, ItemDrop item)
        {
            if (item == null) return;

            if (nview.GetZDO() == null)
                AutomaticFuelPlugin.Destroy(item.gameObject);
            else
                ZNetScene.instance.Destroy(item.gameObject);
        }
    }

    /// <summary>
    /// Wraps the non-public game methods this mod reaches into.
    ///
    /// Every one of these is resolved by NAME ONLY - never by parameter list - so a 1.0-style
    /// signature change can no longer hide the method. The argument array is then sized to
    /// whatever the method asks for today, instead of being hard-coded to zero.
    /// </summary>
    internal static class NonPublicCalls
    {
        private const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<string, MethodInfo> Cache = new Dictionary<string, MethodInfo>();
        private static readonly HashSet<string> Warned = new HashSet<string>();

        private static MethodInfo Resolve(Type type, string name)
        {
            string key = type.FullName + "::" + name;
            if (Cache.TryGetValue(key, out MethodInfo cached)) return cached;

            List<MethodInfo> candidates = new List<MethodInfo>();

            // Most-derived type wins, exactly like Type.GetMethod does.
            for (Type t = type; t != null && candidates.Count == 0; t = t.BaseType)
            {
                try { candidates.AddRange(t.GetMethods(Declared).Where(m => m.Name == name)); }
                catch (Exception) { /* keep walking */ }
            }

            // Prefer the historical parameterless shape, else the smallest parameter list.
            MethodInfo found = candidates.OrderBy(m => m.GetParameters().Length).FirstOrDefault();

            if (found == null)
            {
                AutomaticFuelPlugin.AutomaticFuelLogger.LogError(
                    $"[AutomaticFuel] {type.Name}.{name}() no longer exists - that feature will do nothing.");
            }
            else
            {
                AutomaticFuelPlugin.AutomaticFuelLogger.LogInfo($"[AutomaticFuel] resolved {Describe(found)}");
            }

            Cache[key] = found;
            return found;
        }

        private static string Describe(MethodInfo mi)
        {
            if (mi == null) return "<none>";

            return $"{mi.DeclaringType?.Name}.{mi.Name}(" +
                   string.Join(", ", mi.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")) + ")";
        }

        /// <summary>
        /// Invokes with an argument array matching the real parameter list.
        /// <paramref name="preferred"/> supplies values for leading parameters where known;
        /// anything left over is defaulted. Never throws.
        /// </summary>
        private static object Call(MethodInfo mi, object target, params object[] preferred)
        {
            if (mi == null || target == null) return null;

            ParameterInfo[] ps = mi.GetParameters();
            object[] args = new object[ps.Length];
            bool defaulted = false;

            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                if (pt.IsByRef) pt = pt.GetElementType();

                bool supplied = preferred != null && i < preferred.Length && preferred[i] != null &&
                                ps[i].ParameterType.IsInstanceOfType(preferred[i]);

                if (supplied) args[i] = preferred[i];
                else
                {
                    args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                    defaulted = true;
                }
            }

            if (defaulted && Warned.Add("arg:" + Describe(mi)))
            {
                AutomaticFuelPlugin.AutomaticFuelLogger.LogWarning(
                    $"[AutomaticFuel] {Describe(mi)} now takes {ps.Length} parameter(s); passing defaults so " +
                    "nothing throws. Confirm the correct value for 1.0 and pass it via Call(..., value).");
            }

            try
            {
                return mi.Invoke(target, args);
            }
            catch (Exception e)
            {
                Exception real = (e as TargetInvocationException)?.InnerException ?? e;

                if (Warned.Add("throw:" + Describe(mi)))
                    AutomaticFuelPlugin.AutomaticFuelLogger.LogWarning(
                        $"[AutomaticFuel] {Describe(mi)} threw {real.GetType().Name}: {real.Message}");

                return null;
            }
        }

        /// <summary>Container.Save() + Inventory.Changed() - the two calls that were crashing.</summary>
        internal static void PersistContainer(Container container)
        {
            if (!container) return;

            Call(Resolve(typeof(Container), "Save"), container);

            Inventory inventory = container.GetInventory();
            if (inventory != null)
                Call(Resolve(typeof(Inventory), "Changed"), inventory);
        }

        /// <summary>ItemDrop.Save() - persist a ground stack this mod just decremented.</summary>
        internal static void SaveItemDrop(ItemDrop item)
        {
            if (!item) return;
            Call(Resolve(typeof(ItemDrop), "Save"), item);
        }

        /// <summary>Smelter.GetQueueSize().</summary>
        internal static int QueueSize(Smelter smelter)
        {
            if (!smelter) return 0;

            object result = Call(Resolve(typeof(Smelter), "GetQueueSize"), smelter);
            if (result == null) return 0;

            try { return Convert.ToInt32(result); }
            catch (Exception) { return 0; }
        }
    }
}