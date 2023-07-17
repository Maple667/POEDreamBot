using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using DreamPoeBot.Common;
using DreamPoeBot.Loki;
using DreamPoeBot.Loki.Bot;
using DreamPoeBot.Loki.Bot.Pathfinding;
using DreamPoeBot.Loki.Common;
using DreamPoeBot.Loki.Coroutine;
using DreamPoeBot.Loki.Game;
using DreamPoeBot.Loki.Game.GameData;
using DreamPoeBot.Loki.Game.Objects;
using log4net;


namespace ExampleRoutine
{
    public class ExampleRoutine: IRoutine
    {
        private ExampleRoutineGui _gui;

        public JsonSettings Settings => ExampleRoutineSettings.Instance;
        public UserControl Control => _gui ?? (_gui = new ExampleRoutineGui());
        public string Author => "Alcor75";
        public string Description => "An Example Routine";
        public string Name => "ExampleRoutine";
        public string Version => "1.0";
        public void Deinitialize()
        {
            _combatTargeting.InclusionCalcuation -= CombatTargetingOnInclusionCalcuation;
            _combatTargeting.WeightCalculation -= CombatTargetingOnWeightCalculation;
        }

        public void Initialize()
        {
            // The Initialization function is called during the Bot loading stage, as soon all components are been integrated in the bot. here we execute basic component initializations.
            _combatTargeting.InclusionCalcuation += CombatTargetingOnInclusionCalcuation;
            _combatTargeting.WeightCalculation += CombatTargetingOnWeightCalculation;

            RegisterExposedSettings();
        }

        public async Task<LogicResult> Logic(Logic logic)
        {
            // Something, usually a specific task, requested the Routine to perform this Logic with a `hook_combat` id, here we can implement new id for differents porpose.
            if (logic.Id == "hook_combat")
            {
                // Here we can execute some combat setup actions, like trigger a Stance, cast a aura we absolutly need to be active 100% of the time, etc etc.

                // Now Update the Targeting class, that will process all objects around us (the game see object with a max distance of 210, after that range everithing disappear, the game dont recieve those info from the server)
                CombatTargeting.Update();

                // We now signal always highlight needs to be disabled, but won't actually do it until we cast something.
                _needsToDisableAlwaysHighlight = true;
                var myPos = LokiPoe.MyPosition;
                if (LokiPoe.Me.HasAura("Grace Period"))
                {
                    // This is a moment of Invulnerability you have when you first enter a zone, you can move but you cant cast skills, or you lose your invulnerability.
                    Log.DebugFormat($"Grace period detected!");
                }

                // TODO: _currentLeashRange of -1 means we need to use a cached location system to prevent back and forth issues of mobs despawning.

                // This is pretty important. Otherwise, components can go invalid and exceptions are thrown.
                var bestTarget = CombatTargeting.Targets<Monster>().FirstOrDefault();

                // No monsters, we can execute non-critical combat logic, like buffs, auras, etc...
                // For this example, just going to continue executing bot logic.
                if (bestTarget == null)
                {
                    if (await HandleShrines())
                    {
                        return LogicResult.Provided;
                    }
                    
                    return await CombatLogicEnd();
                }

                var cachedTargetName = bestTarget.Name;
                var cachedTargetPosition = bestTarget.Position;
                var cachedRarity = bestTarget.Rarity;
                var cachedHpPercent = (int)bestTarget.HealthPercentTotal;
                var cachedNumberOfMobsNearTarget = NumberOfMobsNear(bestTarget, 20);
                var cachedProxShield = bestTarget.HasProximityShield || bestTarget.Metadata == "Metadata/Monsters/Tukohama/TukohamaShieldTotem";

                var canMove = true;

                if (await HandleShrines())
                {
                    return LogicResult.Provided;
                }

                var canSee = ExilePather.CanObjectSee(LokiPoe.Me, bestTarget, false);
                var pathDistance = ExilePather.PathDistance(myPos, cachedTargetPosition, false, false);
                var blockedByDoor = ClosedDoorBetween(LokiPoe.Me, bestTarget, 10, 10, false);

                var skipPathing = bestTarget.Rarity == Rarity.Unique &&
                                  (bestTarget.Metadata.Contains("KitavaBoss/Kitava") || bestTarget.Metadata.Contains("VaalSpiderGod/Arakaali"));

                if (pathDistance.CompareTo(float.MaxValue) == 0 && !skipPathing)
                {
                    Log.ErrorFormat(
                        "[Logic] Could not determine the path distance to the best target. Now blacklisting it.");
                    Blacklist.Add(bestTarget.Id, TimeSpan.FromMinutes(1), "Unable to pathfind to.");
                    return LogicResult.Provided;
                }

                // Prevent combat loops from happening by preventing combat outside CombatRange.
                if (pathDistance > ExampleRoutineSettings.Instance.CombatRange && !skipPathing)
                {
                    await EnableAlwaysHiglight();

                    return LogicResult.Unprovided;
                }

                if ((!canSee && !skipPathing) || blockedByDoor)
                {
                    if (!canMove)
                    {
                        Log.InfoFormat("[Logic] Not moving towards the target because we should not move currently.");

                        return LogicResult.Unprovided;
                    }
                    else
                    {
                        Log.InfoFormat(
                            "[Logic] Now moving towards the monster {0} because [canSee: {1}][pathDistance: {2}][blockedByDoor: {3}]",
                            cachedTargetName, canSee, pathDistance, blockedByDoor);
                        LokiPoe.ProcessHookManager.ClearAllKeyStates();
                        var moveSuccess = PlayerMoverManager.Current.MoveTowards(cachedTargetPosition);
                        if (!moveSuccess)
                        {
                            Log.ErrorFormat("[Logic] MoveTowards failed for {0}.", cachedTargetPosition);
                            await Coroutines.FinishCurrentAction();
                        }
                        return LogicResult.Provided;
                    }
                }

                // Here we decide what skil to use based on configuration and current situation:
                var aip = false;
                var aoe = false;
                var melee = false;
                int slot = -1;

                // Logic for figuring out if we should use an AoE skill instead.
                if (myPos.Distance(cachedTargetPosition) < ExampleRoutineSettings.Instance.MaxMeleeRange)
                {
                    melee = true;
                    if (cachedNumberOfMobsNearTarget >= ExampleRoutineSettings.Instance.AoePackSize)
                    {
                        aoe = true;
                    }
                    else
                    {
                        aoe = false;
                    }
                }
                // This sillyness is for making sure we always use a skill, and is why generic code is a PITA
                // when it can be configured like so.
                KeyValuePair<int, bool> skillAndRange = await FindSkillAndRange(aoe, melee);
                slot = skillAndRange.Key;
                melee = skillAndRange.Value;

                if (ExampleRoutineSettings.Instance.AlwaysAttackInPlace)
                    aip = true;

                if (slot == -1)
                {
                    slot = ExampleRoutineSettings.Instance.FallbackSlot;
                    melee = true;
                }

                if (slot == -1)
                {
                    Log.ErrorFormat("[Logic] There is no slot configured to use.");
                    return LogicResult.Provided;
                }

                if (cachedProxShield)
                {
                    var dist = LokiPoe.MyPosition.Distance(cachedTargetPosition);
                    if (dist > 20)
                    {
                        if (skipPathing)
                        {
                            Log.Info($"[Logic] Cannot move towards {cachedTargetName}. We will rely on QuestBot to bring us close to him.");
                            return LogicResult.Unprovided;
                        }

                        if (!canMove)
                        {
                            Log.InfoFormat("[Logic] Not moving towards the target because we should not move currently.");

                            return LogicResult.Unprovided;
                        }

                        Log.InfoFormat("[Logic] Now moving towards {0} because [Target has ProxShield]", cachedTargetPosition);
                        LokiPoe.ProcessHookManager.ClearAllKeyStates();
                        if (!PlayerMoverManager.Current.MoveTowards(cachedTargetPosition))
                        {
                            Log.ErrorFormat("[Logic] MoveTowards failed for {0}.", cachedTargetPosition);
                            await Coroutines.FinishCurrentAction();
                        }
                        return LogicResult.Provided;
                    }
                }

                if (melee)
                {
                    var dist = LokiPoe.MyPosition.Distance(cachedTargetPosition);
                    if (dist > ExampleRoutineSettings.Instance.MaxMeleeRange)
                    {
                        if (skipPathing)
                        {
                            Log.Info($"[Logic] Cannot move towards {cachedTargetName}. We will rely on QuestBot to bring us close to him.");
                            return LogicResult.Unprovided;
                        }

                        if (!canMove)
                        {
                            Log.InfoFormat("[Logic] Not moving towards the target because we should not move currently.");

                            return LogicResult.Unprovided;
                        }

                        Log.InfoFormat("[Logic] Now moving towards {0} because [dist ({1}) > MaxMeleeRange ({2})]",
                            cachedTargetPosition, dist, ExampleRoutineSettings.Instance.MaxMeleeRange);
                        LokiPoe.ProcessHookManager.ClearAllKeyStates();
                        if (!PlayerMoverManager.Current.MoveTowards(cachedTargetPosition))
                        {
                            Log.ErrorFormat("[Logic] MoveTowards failed for {0}.", cachedTargetPosition);
                            await Coroutines.FinishCurrentAction();
                        }
                        return LogicResult.Provided;
                    }
                }
                else
                {
                    var dist = LokiPoe.MyPosition.Distance(cachedTargetPosition);
                    if (dist > ExampleRoutineSettings.Instance.MaxRangeRange)
                    {
                        if (skipPathing)
                        {
                            Log.Info($"[Logic] Cannot move towards {cachedTargetName}. We will rely on QuestBot to bring us close to him.");
                            return LogicResult.Unprovided;
                        }

                        if (!canMove)
                        {
                            Log.InfoFormat("[Logic] Not moving towards the target because we should not move currently.");

                            return LogicResult.Unprovided;
                        }

                        Log.InfoFormat("[Logic] Now moving towards {0} because [dist ({1}) > MaxRangeRange ({2})]",
                            cachedTargetPosition, dist, ExampleRoutineSettings.Instance.MaxRangeRange);
                        LokiPoe.ProcessHookManager.ClearAllKeyStates();

                        if (!PlayerMoverManager.Current.MoveTowards(cachedTargetPosition))
                        {
                            Log.ErrorFormat("[Logic] MoveTowards failed for {0}.", cachedTargetPosition);
                            await Coroutines.FinishCurrentAction();
                        }
                        return LogicResult.Provided;
                    }
                }

                await DisableAlwaysHiglight();

                if (_flaskHook != null && _flaskHook(cachedRarity, cachedHpPercent))
                {
                    return LogicResult.Provided;
                }

                var slotSkill = LokiPoe.InGameState.SkillBarHud.Slot(slot);
                if (slotSkill == null)
                {
                    Log.ErrorFormat("[Logic] There is no skill in the slot configured to use.");
                    return LogicResult.Provided;
                }
                LokiPoe.ProcessHookManager.ClearAllKeyStates();

                var randomPos = cachedTargetPosition + new Vector2i(LokiPoe.Random.Next(-5, 5), LokiPoe.Random.Next(-5, 5));

                var err = LokiPoe.InGameState.SkillBarHud.BeginUseAt(slot, aip, randomPos);
                if (err != LokiPoe.InGameState.UseResult.None)
                {
                    Log.WarnFormat("[Logic] BeginUseAt returned {0}.", err);
                }
                
                return LogicResult.Provided;
            }
            return LogicResult.Unprovided;
        }

        public MessageResult Message(Message message)
        {
            // This is the Message system to generically let all bot components to read this Routine Settings, this is just an Example.
            Func<Tuple<object, string>[], object> f;
            if (_exposedSettings.TryGetValue(message.Id, out f))
            {
                message.AddOutput(this, f(message.Inputs.ToArray()));
                return MessageResult.Processed;
            }

            // Retrive the CombatTargeting 
            if (message.Id == "GetCombatTargeting")
            {
                message.AddOutput(this, CombatTargeting);
                return MessageResult.Processed;
            }
            // Reset the CombatTargeting 
            if (message.Id == "ResetCombatTargeting")
            {
                _combatTargeting.ResetInclusionCalcuation();
                _combatTargeting.ResetWeightCalculation();
                _combatTargeting.InclusionCalcuation += CombatTargetingOnInclusionCalcuation;
                _combatTargeting.WeightCalculation += CombatTargetingOnWeightCalculation;
                return MessageResult.Processed;
            }

            // If some components of the bot implement this message, here we react to it.
            if (message.Id == "player_leveled_event")
            {
                Log.InfoFormat("[Logic] We are now level {0}!", message.GetInput<int>());
                return MessageResult.Processed;
            }

            if (message.Id == "SetLeash")
            {
                var range = message.GetInput<int>();
                _currentLeashRange = range;
                return MessageResult.Processed;
            }

            if (message.Id == "SetCombatRange")
            {
                var range = message.GetInput<int>();
                ExampleRoutineSettings.Instance.CombatRange = range;
                Log.Info($"[ExampleRoutine] Combat range is set to {range}");
                return MessageResult.Processed;
            }
            if (message.Id == "GetCombatRange")
            {
                message.AddOutput(this, ExampleRoutineSettings.Instance.CombatRange);
                return MessageResult.Processed;
            }
            if (message.Id == "SetFlaskHook")
            {
                var func = message.GetInput<Func<Rarity, int, bool>>();
                _flaskHook = func;
                Log.Info("[Alcor75CombatRoutine] Flask hook has been set.");
                return MessageResult.Processed;
            }
            return MessageResult.Unprocessed;
        }

        public void Start()
        {
            if (ExampleRoutineSettings.Instance.SingleTargetMeleeSlot == -1 &&
                ExampleRoutineSettings.Instance.SingleTargetRangedSlot == -1 &&
                ExampleRoutineSettings.Instance.AoeMeleeSlot == -1 &&
                ExampleRoutineSettings.Instance.AoeRangedSlot == -1 &&
                ExampleRoutineSettings.Instance.FallbackSlot == -1
            )
            {
                Log.ErrorFormat(
                    "[Start] Please configure the ExampleRoutine settings (Settings -> ExampleRoutine) before starting!");
                BotManager.Stop();
            }
        }

        public void Stop()
        {
            
        }

        public void Tick()
        {
            
        }

        // List of Routine specific Members and Methods:
        private static readonly ILog Log = Logger.GetLoggerInstanceForType();
        private int _currentLeashRange = -1;

        // The targeting class is an helper class that can Mantain and Update a list of Monster ordered and filtered by 2 events functions:
        // CombatTargetingOnInclusionCalcuation Fired by the CombatTargeting.Update() function, during the Routine execution, usually we decide if to add the object to the possible targets list.
        // CombatTargetingOnWeightCalculation Fired by the CombatTargeting.Update() function, during the Routine execution, here we assign a weight to this target based on the function logic.
        // Upon calling `CombatTargeting.Update()` the 2 events fire and create a Weight Ordered list of targets.
        // The List is retrived with the `CombatTargeting.Targets<Monster>()` function.
        public Targeting CombatTargeting => _combatTargeting;
        private readonly Targeting _combatTargeting = new Targeting();
        private bool _needsToDisableAlwaysHighlight;
        private Dictionary<string, Func<Tuple<object, string>[], object>> _exposedSettings;
        private readonly Dictionary<int, List<ushort>> _corpseSkillBlacklist = new Dictionary<int, List<ushort>>();
        private readonly List<int> _ignoreAnimatedItems = new List<int>();
        private readonly string[] _aurasToIgnore = new[]
        {
            "shrine_godmode", // Ignore any mob near Divine Shrine
            "bloodlines_invulnerable", // Ignore Phylacteral Link
            "god_mode", // Ignore Animated Guardian
            "bloodlines_necrovigil",
        };
        // Flask hook, this is can be used by your Flask Plugin to know if the routine is about to attack something and eventuallu use a flask before to perform the attack.
        private Func<Rarity, int, bool> _flaskHook;

        // Lab variables
        private bool _figthIdols;
        public WorldItem BestAnimateGuardianTarget(Monster monster, int maxLevel)
        {
            var worldItems = LokiPoe.ObjectManager.GetObjectsByType<WorldItem>()
                .Where(wi => !_ignoreAnimatedItems.Contains(wi.Id) && wi.Distance < 30)
                .OrderBy(wi => wi.Distance);
            foreach (var wi in worldItems)
            {
                var item = wi.Item;
                if (item.RequiredLevel <= maxLevel &&
                    item.IsIdentified &&
                    !item.IsChromatic &&
                    item.SocketCount < 5 &&
                    item.MaxLinkCount < 5 &&
                    item.Rarity <= Rarity.Magic
                )
                {
                    if (monster == null || monster.LeftHandWeaponVisual == "")
                    {
                        if (item.AnyMetadataFlags(MetadataFlags.Claws, MetadataFlags.OneHandAxes, MetadataFlags.OneHandMaces,
                            MetadataFlags.OneHandSwords, MetadataFlags.OneHandThrustingSwords, MetadataFlags.TwoHandAxes,
                            MetadataFlags.TwoHandMaces, MetadataFlags.TwoHandSwords))
                        {
                            _ignoreAnimatedItems.Add(wi.Id);
                            return wi;
                        }
                    }

                    if (monster == null || monster.ChestVisual == "")
                    {
                        if (item.HasMetadataFlags(MetadataFlags.BodyArmours))
                        {
                            _ignoreAnimatedItems.Add(wi.Id);
                            return wi;
                        }
                    }

                    if (monster == null || monster.HelmVisual == "")
                    {
                        if (item.HasMetadataFlags(MetadataFlags.Helmets))
                        {
                            _ignoreAnimatedItems.Add(wi.Id);
                            return wi;
                        }
                    }

                    if (monster == null || monster.BootsVisual == "")
                    {
                        if (item.HasMetadataFlags(MetadataFlags.Boots))
                        {
                            _ignoreAnimatedItems.Add(wi.Id);
                            return wi;
                        }
                    }

                    if (monster == null || monster.GlovesVisual == "")
                    {
                        if (item.HasMetadataFlags(MetadataFlags.Gloves))
                        {
                            _ignoreAnimatedItems.Add(wi.Id);
                            return wi;
                        }
                    }
                }
            }

            return null;
        }

        private WorldItem BestAnimateWeaponTarget(int maxLevel)
        {
            var worldItems = LokiPoe.ObjectManager.GetObjectsByType<WorldItem>()
                .Where(wi => !_ignoreAnimatedItems.Contains(wi.Id) && wi.Distance < 30)
                .OrderBy(wi => wi.Distance);
            foreach (var wi in worldItems)
            {
                var item = wi.Item;
                if (item.IsIdentified &&
                    item.RequiredLevel <= maxLevel &&
                    !item.IsChromatic &&
                    item.SocketCount < 5 &&
                    item.MaxLinkCount < 5 &&
                    item.Rarity <= Rarity.Magic &&
                    item.AnyMetadataFlags(MetadataFlags.Claws, MetadataFlags.OneHandAxes, MetadataFlags.OneHandMaces,
                        MetadataFlags.OneHandSwords, MetadataFlags.OneHandThrustingSwords, MetadataFlags.TwoHandAxes,
                        MetadataFlags.TwoHandMaces, MetadataFlags.TwoHandSwords, MetadataFlags.Daggers, MetadataFlags.Staves))
                {
                    _ignoreAnimatedItems.Add(wi.Id);
                    return wi;
                }
            }
            return null;
        }

        private void BlacklistCorpseForSkill(int corpseId, ushort skillId)
        {
            if (!_corpseSkillBlacklist.ContainsKey(corpseId))
                _corpseSkillBlacklist.Add(corpseId, new List<ushort>());
            if (_corpseSkillBlacklist[corpseId].Any(x => x == skillId)) return;
            _corpseSkillBlacklist[corpseId].Add(skillId);
        }
        private Monster BestDeadTarget(ushort skillId)
        {
            var myPos = LokiPoe.MyPosition;
            var corpses = LokiPoe.ObjectManager.GetObjectsByType<Monster>().Where(x => x != null);
            List<Monster> monsters = new List<Monster>();
            foreach (var corpse in corpses)
            {
                try
                {
                    if (_corpseSkillBlacklist.ContainsKey(corpse.Id))
                    {
                        if (_corpseSkillBlacklist[corpse.Id].Any(x => x == skillId)) continue;
                    }
                    if (corpse.Components.PositionedComponent == null) continue;
                    if (!corpse.IsActiveDead) continue;
                    if (corpse.Rarity == Rarity.Unique) continue;
                    if (!corpse.CorpseUsable) continue;
                    if (corpse.Distance >= 35) continue;
                    if (!ExilePather.CanObjectSee(LokiPoe.Me, corpse)) continue;
                    if (ExilePather.PathDistance(myPos, corpse.Position) >= 35) continue;
                    monsters.Add(corpse);
                }
                catch (Exception)
                {

                }
            }
            return monsters.OrderBy(m => m.Distance)
                .FirstOrDefault();
        }
        private Monster BestSpecificDeadTarget(ushort skillId)
        {
            var myPos = LokiPoe.MyPosition;
            var corpses = LokiPoe.ObjectManager.GetObjectsByType<Monster>().Where(x => x != null);
            List<Monster> monsters = new List<Monster>();
            foreach (var corpse in corpses)
            {
                try
                {
                    if (_corpseSkillBlacklist.ContainsKey(corpse.Id))
                    {
                        if (_corpseSkillBlacklist[corpse.Id].Any(x => x == skillId)) continue;
                    }
                    if (corpse.Components.PositionedComponent == null) continue;
                    if (!corpse.IsActiveDead) continue;
                    if (corpse.Rarity == Rarity.Unique) continue;
                    if (!corpse.CorpseUsable) continue;
                    if (corpse.Distance >= 35) continue;
                    if (!ExilePather.CanObjectSee(LokiPoe.Me, corpse)) continue;
                    if (ExilePather.PathDistance(myPos, corpse.Position) >= 35) continue;
                    monsters.Add(corpse);
                }
                catch (Exception)
                {

                }
            }
            return monsters.OrderBy(m => m.Distance)
                .FirstOrDefault();
        }
        private async Task<LogicResult> CombatLogicEnd()
        {
            var res = LogicResult.Unprovided;

            if (await EnableAlwaysHiglight())
            {
                res = LogicResult.Provided;
            }
            return res;
        }

        private readonly Dictionary<int, int> _shrineTries = new Dictionary<int, int>();

        private async Task<bool> HandleShrines()
        {
            // If the user wants to avoid shrine logic due to stuck issues, simply return without doing anything.
            if (ExampleRoutineSettings.Instance.SkipShrines)
                return false;

            // TODO: Shrines need speical CR logic, because it's now the CRs responsibility for handling all combaat situations,
            // and shrines are now considered a combat situation due their nature.

            // Check for any active shrines.
            var shrines =
                LokiPoe.ObjectManager.Objects.OfType<Shrine>()
                    .Where(s => !Blacklist.Contains(s.Id) && !s.IsDeactivated && s.Distance < 50)
                    .OrderBy(s => s.Distance)
                    .ToList();

            if (!shrines.Any())
                return false;

            Log.InfoFormat("[HandleShrines]");

            // For now, just take the first shrine found.

            var shrine = shrines[0];
            int tries;

            if (!_shrineTries.TryGetValue(shrine.Id, out tries))
            {
                tries = 0;
                _shrineTries.Add(shrine.Id, tries);
            }

            if (tries > 10)
            {
                Blacklist.Add(shrine.Id, TimeSpan.FromHours(1), "Could not interact with the shrine.");

                return true;
            }

            // Handle Skeletal Shrine in a special way, or handle priority between multiple shrines at the same time.
            var skellyOverride = shrine.ShrineId == "Skeletons";

            // Try and only move to touch it when we have a somewhat navigable path.
            if ((NumberOfMobsBetween(LokiPoe.Me, shrine, 5, true) < 5 && NumberOfMobsNear(LokiPoe.Me, 20) < 3) || skellyOverride)
            {
                var myPos = LokiPoe.MyPosition;

                var pos = ExilePather.FastWalkablePositionFor(shrine);

                // We need to filter out based on pathfinding, since otherwise, a large gap will lockup the bot.
                var pathDistance = ExilePather.PathDistance(myPos, pos);

                Log.DebugFormat("[HandleShrines] Now moving towards the Shrine {0} [pathPos: {1} pathDis: {2}].", shrine.Id, pos,
                    pathDistance);

                if (pathDistance > 50)
                {
                    Log.DebugFormat("[HandleShrines] Not attempting to move towards Shrine [{0}] because the path distance is: {1}.",
                        shrine.Id, pathDistance);
                    return false;
                }

                // We're in distance when we're sure we're close to the position, but also that the path we need to take to the position
                // isn't too much further. This prevents problems with things on higher ground when we are on lower, and vice-versa.
                var inDistance = myPos.Distance(pos) < 20 && pathDistance < 25;
                if (inDistance)
                {
                    Log.DebugFormat("[HandleShrines] Now attempting to interact with the Shrine {0}.", shrine.Id);

                    await Coroutines.FinishCurrentAction();

                    await Coroutines.InteractWith(shrine);

                    _shrineTries[shrine.Id]++;
                }
                else
                {
                    LokiPoe.ProcessHookManager.ClearAllKeyStates();
                    if (!PlayerMoverManager.Current.MoveTowards(pos))
                    {
                        Log.ErrorFormat("[HandleShrines] MoveTowards failed for {0}.", pos);

                        Blacklist.Add(shrine.Id, TimeSpan.FromHours(1), "Could not move towards the shrine.");

                        await Coroutines.FinishCurrentAction();
                    }
                }

                return true;
            }

            return false;
        }
        
        // This logic is now CR specific, because Strongbox gui labels interfere with targeting,
        // but not general movement using Move only.
        private async Task DisableAlwaysHiglight()
        {
            if (_needsToDisableAlwaysHighlight && LokiPoe.ConfigManager.IsAlwaysHighlightEnabled)
            {
                Log.InfoFormat("[DisableAlwaysHiglight] Now disabling Always Highlight to avoid skill use issues.");
                LokiPoe.Input.SimulateKeyEvent(LokiPoe.Input.Binding.highlight_toggle, true, false, false);
                await Coroutine.Sleep(16);
            }
        }

        // This logic is now CR specific, because Strongbox gui labels interfere with targeting,
        // but not general movement using Move only.
        private async Task<bool> EnableAlwaysHiglight()
        {
            if (!LokiPoe.ConfigManager.IsAlwaysHighlightEnabled)
            {
                Log.InfoFormat("[EnableAlwaysHiglight] Now enabling Always Highlight.");
                LokiPoe.Input.SimulateKeyEvent(LokiPoe.Input.Binding.highlight_toggle, true, false, false);
                await Coroutine.Sleep(16);
                return true;
            }
            return false;
        }

        private int EnsurceCast(int slot)
        {
            if (slot == -1)
                return slot;

            var slotSkill = LokiPoe.InGameState.SkillBarHud.Slot(slot);
            if (slotSkill == null)
            {
                return -1;
            }

            if (slotSkill.Name == "Flicker Strike" && (slotSkill.CanUse() || LokiPoe.Me.FrenzyCharges > 0))
            {
                return slot;
            }
            if (!slotSkill.CanUse())
            {
                return -1;
            }

            return slot;
        }

        private bool CombatTargetingOnInclusionCalcuation(NetworkObject entity)
        {
            try
            {
                var m = entity as Monster;
                if (m == null)
                    return false;

                if (Blacklist.Contains(m))
                    return false;

                // Do not consider inactive/dead mobs.
                if (!m.IsActive)
                    return false;

                // Ignore any mob that cannot die.
                if (m.CannotDie)
                    return false;

                // Always include Tukohama Shield Totem
                if (m.Metadata == "Metadata/Monsters/Tukohama/TukohamaShieldTotem")
                {
                    return true;
                }
                
                if (m.Metadata.Contains("Monsters/Totems/LabyrinthPopUpTotem"))
                    return false;

                // Ignore mobs that are too far to care about.
                if (m.Distance > (_currentLeashRange != -1 ? _currentLeashRange : ExampleRoutineSettings.Instance.CombatRange))
                    return false;

                // Ignore mobs with special aura/buffs
                if (m.HasAura(_aurasToIgnore))
                    return false;

                if (m.Name == "Ryslatha's Brood Egg")
                    return false;

                if (m.Name == "Fire Fury")
                    return false;

                if (m.Name == "The Burning Man")
                    return false;

                if (m.Name == "Wandering Eye")
                    return false;

                if (m.Name == "Eye Hatchery")
                    return false;
                
                // Ignore these mobs when trying to transition in the dom fight.
                if (m.Name == "Miscreation")
                {
                    var dom = LokiPoe.ObjectManager.GetObjectByName<Monster>("Dominus, High Templar");
                    if (dom != null && !dom.IsDead &&
                        (dom.Components.TransitionableComponent.Flag1 == 6 || dom.Components.TransitionableComponent.Flag1 == 5))
                    {
                        Blacklist.Add(m.Id, TimeSpan.FromHours(1), "Miscreation");
                        return false;
                    }
                }

                // Ignore Piety's portals.
                if (m.Name == "Chilling Portal" || m.Name == "Burning Portal")
                {
                    Blacklist.Add(m.Id, TimeSpan.FromHours(1), "Piety portal");
                    return false;
                }

                if (m.Metadata.Contains("DoedreStonePillar"))
                {
                    Blacklist.Add(m.Id, TimeSpan.FromHours(1), "Doedre Pillar");
                    return false;
                }

                if (m.HasBestiaryCapturedAura || m.HasBestiaryDisappearingAura)
                    return false;
            }
            catch (Exception ex)
            {
                Log.Error("[CombatOnInclusionCalcuation]", ex);
                return false;
            }
            return true;
        }

        private void CombatTargetingOnWeightCalculation(NetworkObject entity, ref float weight)
        {
            var m = entity as Monster;
            if (m == null)
                return;

            // If the monster is the source of Allies Cannot Die, we really want to kill it fast.
            if (m.HasAura("monster_aura_cannot_die"))
                weight += 50;

            /*if (m.IsTargetingMe)
			{
				weight += 20;
			}*/

            if (m.Rarity == Rarity.Magic)
            {
                weight += 15;
            }
            else if (m.Rarity == Rarity.Rare)
            {
                weight += 25;
            }
            else if (m.Rarity == Rarity.Unique)
            {
                weight += 70;
            }

            // Minions that get in the way.
            switch (m.Name)
            {
                case "Summoned Skeleton":
                    weight -= 15;
                    break;

                case "Raised Zombie":
                    weight -= 15;
                    break;
            }

            if (m.Rarity == Rarity.Normal && m.Type.Contains("/Totems/"))
            {
                weight -= 15;
            }

            // Necros
            if (m.ExplicitAffixes.Any(a => a.InternalName.Contains("RaisesUndead")) ||
                m.ImplicitAffixes.Any(a => a.InternalName.Contains("RaisesUndead")))
            {
                weight += 45;
            }

            // Ignore these mostly, as they just respawn.
            if (m.Type.Contains("TaniwhaTail"))
            {
                weight -= 30;
            }

            // Ignore mobs that expire and die
            if (m.Components.DiesAfterTimeComponent != null)
            {
                weight -= 15;
            }

            // Make sure hearts are targeted with highest priority.
            if (m.Type.Contains("/BeastHeart"))
            {
                weight += 75;
            }

            if (m.Metadata == "Metadata/Monsters/Tukohama/TukohamaShieldTotem")
            {
                weight += 75;
            }

            if (m.IsStrongboxMinion || m.IsHarbingerMinion)
            {
                weight += 30;
            }
            if (m.IsBreachMonster)
            {
                weight += 100;
            }
            if (m.IsCorruptedMissionBeast)
            {
                weight += 100;
            }
            if (m.IsMissionMob)
            {
                weight += 100;
            }
            if (m.Name == "Izaro")
            {
                weight += 500;
            }
            if (m.IsActive && (m.Name == "Crackling Essence" || m.Name == "Freezing Essence" || m.Name == "Burning Essence"))
            {
                weight += 1000;
            }
            if (m.IsActive && (m.Name == "Font of Elements" || m.Name == "Font of Fragility" || m.Name == "Font of Lethargy"))
            {
                weight += 1000;
            }
            if (m.IsActive && (m.Name == "Steel-Imbued Gargoyle" || m.Name == "Granite-Imbued Gargoyle" || m.Name == "Quicksilver-Imbued Gargoyle"))
            {
                weight += 1000;
            }
            if (m.IsActive && (m.Name == "Lieutenant of Rage" || m.Name == "Lieutenant of the Bow" || m.Name == "Lieutenant of the Mace"))
            {
                weight += 1000;
            }
            if (m.IsActive && (m.Name == "Threshold of Wrath" || m.Name == "Threshold of Hatred" || m.Name == "Threshold of Anger"))
            {
                weight += 1000;
            }
            if (m.IsActive && (m.Name == "Frost Idol" || m.Name == "Storm Idol" || m.Name == "Flame Idol"))
            {
                if (!_figthIdols || m.HealthPercent < 10)
                {
                    _figthIdols = false;
                    weight += -1000;
                }
                if (_figthIdols || m.HealthPercent > 60)
                {
                    _figthIdols = true;
                    weight += 1000;
                }
            }
            if (LokiPoe.InstanceInfo.Ritual.IsRitualActive)
            {
                var ritualLight = LokiPoe.ObjectManager.Objects.Find(o => o.Metadata.Contains("RitualRuneInteractable"));
                if (ritualLight != null)
                {
                    if (m.Position.Distance(ritualLight.Position) > 95)
                        weight -= 10000;
                }

            }
        }

        /// <summary>
		/// 
		/// </summary>
		/// <param name="start"></param>
		/// <param name="end"></param>
		/// <param name="distanceFromPoint"></param>
		/// <param name="dontLeaveFrame">Should the current frame not be left?</param>
		/// <returns></returns>
		public static int NumberOfMobsBetween(NetworkObject start, NetworkObject end, int distanceFromPoint = 5, bool dontLeaveFrame = false)
        {
            // More lightweight check to just get an idea of what is around us, rather than the heavy IsActive.
            var mobPositions =
                LokiPoe.ObjectManager.GetObjectsByType<Monster>().Where(d => d.IsAliveHostile).Select(m => m.Position).ToList();
            if (!mobPositions.Any())
                return 0;

            var path = ExilePather.GetPointsOnSegment(start.Position, end.Position, dontLeaveFrame);

            var count = 0;
            for (var i = 0; i < path.Count; i += 10)
            {
                foreach (var mobPosition in mobPositions)
                {
                    if (mobPosition.Distance(path[i]) <= distanceFromPoint)
                    {
                        ++count;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Checks for a closed door between start and end.
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <param name="distanceFromPoint">How far to check around each point for a door object.</param>
        /// <param name="stride">The distance between points to check in the path.</param>
        /// <param name="dontLeaveFrame">Should the current frame not be left?</param>
        /// <returns>true if there's a closed door and false otherwise.</returns>
        public static bool ClosedDoorBetween(NetworkObject start, NetworkObject end, int distanceFromPoint = 10, int stride = 10, bool dontLeaveFrame = false)
        {
            return ClosedDoorBetween(start.Position, end.Position, distanceFromPoint, stride, dontLeaveFrame);
        }
        public static bool ClosedDoorBetween(Entity start, NetworkObject end, int distanceFromPoint = 10, int stride = 10, bool dontLeaveFrame = false)
        {
            return ClosedDoorBetween(start.Position, end.Position, distanceFromPoint, stride, dontLeaveFrame);
        }
        /// <summary>
        /// Checks for a closed door between start and end.
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <param name="distanceFromPoint">How far to check around each point for a door object.</param>
        /// <param name="stride">The distance between points to check in the path.</param>
        /// <param name="dontLeaveFrame">Should the current frame not be left?</param>
        /// <returns>true if there's a closed door and false otherwise.</returns>
        public static bool ClosedDoorBetween(NetworkObject start, Vector2i end, int distanceFromPoint = 10, int stride = 10, bool dontLeaveFrame = false)
        {
            return ClosedDoorBetween(start.Position, end, distanceFromPoint, stride, dontLeaveFrame);
        }
        public static bool ClosedDoorBetween(Entity start, Vector2i end, int distanceFromPoint = 10, int stride = 10, bool dontLeaveFrame = false)
        {
            return ClosedDoorBetween(start.Position, end, distanceFromPoint, stride, dontLeaveFrame);
        }
        /// <summary>
        /// Checks for a closed door between start and end.
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <param name="distanceFromPoint">How far to check around each point for a door object.</param>
        /// <param name="stride">The distance between points to check in the path.</param>
        /// <param name="dontLeaveFrame">Should the current frame not be left?</param>
        /// <returns>true if there's a closed door and false otherwise.</returns>
        public static bool ClosedDoorBetween(Vector2i start, NetworkObject end, int distanceFromPoint = 10, int stride = 10, bool dontLeaveFrame = false)
        {
            return ClosedDoorBetween(start, end.Position, distanceFromPoint, stride, dontLeaveFrame);
        }

        /// <summary>
        /// Checks for a closed door between start and end.
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <param name="distanceFromPoint">How far to check around each point for a door object.</param>
        /// <param name="stride">The distance between points to check in the path.</param>
        /// <param name="dontLeaveFrame">Should the current frame not be left?</param>
        /// <returns>true if there's a closed door and false otherwise.</returns>
        public static bool ClosedDoorBetween(Vector2i start, Vector2i end, int distanceFromPoint = 10, int stride = 10, bool dontLeaveFrame = false)
        {
            // We need to store positions and not objects to avoid frame leaving issues.
            var doorPositions = LokiPoe.ObjectManager.AnyDoors.Where(d => !d.IsOpened).Select(d => d.Position).ToList();
            if (!doorPositions.Any())
                return false;

            var path = ExilePather.GetPointsOnSegment(start, end, dontLeaveFrame);

            for (var i = 0; i < path.Count; i += stride)
            {
                foreach (var doorPosition in doorPositions)
                {
                    if (doorPosition.Distance(path[i]) <= distanceFromPoint)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Returns the number of mobs near a target.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="distance"></param>
        /// <param name="dead"></param>
        /// <returns></returns>
        private static int NumberOfMobsNear(NetworkObject target, float distance, bool dead = false)
        {
            var mpos = target.Position;

            var mobPositions =
                LokiPoe.ObjectManager.GetObjectsByType<Monster>().Where(d => d.IsAliveHostile && d.Id != target.Id).Select(m => m.Position).ToList();
            if (!mobPositions.Any())
                return 0;

            var curCount = 0;

            foreach (var mobPosition in mobPositions)
            {
                if (mobPosition.Distance(mpos) < distance)
                {
                    curCount++;
                }
            }

            return curCount;
        }
        private static int NumberOfMobsNear(Entity target, float distance, bool dead = false)
        {
            var mpos = target.Position;

            var mobPositions =
                LokiPoe.ObjectManager.GetObjectsByType<Monster>().Where(d => d.IsAliveHostile && d.Id != target.Id).Select(m => m.Position).ToList();
            if (!mobPositions.Any())
                return 0;

            var curCount = 0;

            foreach (var mobPosition in mobPositions)
            {
                if (mobPosition.Distance(mpos) < distance)
                {
                    curCount++;
                }
            }

            return curCount;
        }
        public static int NumberOfMobsBetween(Entity start, NetworkObject end, int distanceFromPoint = 5, bool dontLeaveFrame = false)
        {
            // More lightweight check to just get an idea of what is around us, rather than the heavy IsActive.
            var mobPositions =
                LokiPoe.ObjectManager.GetObjectsByType<Monster>().Where(d => d.IsAliveHostile).Select(m => m.Position).ToList();
            if (!mobPositions.Any())
                return 0;

            var path = ExilePather.GetPointsOnSegment(start.Position, end.Position, dontLeaveFrame);

            var count = 0;
            for (var i = 0; i < path.Count; i += 10)
            {
                foreach (var mobPosition in mobPositions)
                {
                    if (mobPosition.Distance(path[i]) <= distanceFromPoint)
                    {
                        ++count;
                    }
                }
            }

            return count;
        }
        private static KeyValuePair<int, Rarity> NumberOfMobsNearAndRarity(NetworkObject target, float distance, bool dead = false)
        {
            var mpos = target.Position;

            var mobs =
                LokiPoe.ObjectManager.GetObjectsByType<Monster>().Where(d => d.IsAliveHostile && d.Id != target.Id).Select(m => new { m.Position, m.Rarity }).ToList();
            if (!mobs.Any())
                return new KeyValuePair<int, Rarity>(0, Rarity.Normal);

            var curCount = 0;
            Rarity curRarity = Rarity.Normal;
            foreach (var mob in mobs)
            {
                if (!(mob.Position.Distance(mpos) < distance)) continue;
                curCount++;
                if (mob.Rarity > curRarity)
                    curRarity = mob.Rarity;
            }

            return new KeyValuePair<int, Rarity>(curCount, curRarity);
        }
        private static KeyValuePair<int, Rarity> NumberOfMobsNearAndRarity(Entity target, float distance, bool dead = false)
        {
            var mpos = target.Position;

            var mobs =
                LokiPoe.ObjectManager.GetObjectsByType<Monster>().Where(d => d.IsAliveHostile && d.Id != target.Id).Select(m => new { m.Position, m.Rarity }).ToList();
            if (!mobs.Any())
                return new KeyValuePair<int, Rarity>(0, Rarity.Normal);

            var curCount = 0;
            Rarity curRarity = Rarity.Normal;
            foreach (var mob in mobs)
            {
                if (!(mob.Position.Distance(mpos) < distance)) continue;
                curCount++;
                if (mob.Rarity > curRarity)
                    curRarity = mob.Rarity;
            }

            return new KeyValuePair<int, Rarity>(curCount, curRarity);
        }

        private async Task<KeyValuePair<int, bool>> FindSkillAndRange(bool aoe, bool melee)
        {
            var _slot = -1;
            var _melee = melee;
            if (aoe)
            {
                if (melee)
                {
                    if (ExampleRoutineSettings.Instance.AoeMeleeSlot != -1)
                    {
                        _slot = EnsurceCast(ExampleRoutineSettings.Instance.AoeMeleeSlot);
                    }
                    if (_slot == -1)
                    {
                        if (ExampleRoutineSettings.Instance.SingleTargetMeleeSlot != -1)
                        {
                            _slot = EnsurceCast(ExampleRoutineSettings.Instance.SingleTargetMeleeSlot);
                        }
                        if (_slot == -1)
                        {
                            if (ExampleRoutineSettings.Instance.AoeRangedSlot != -1)
                            {
                                _melee = false;
                                _slot = EnsurceCast(ExampleRoutineSettings.Instance.AoeRangedSlot);
                            }
                            if (_slot == -1)
                            {
                                if (ExampleRoutineSettings.Instance.SingleTargetRangedSlot != -1)
                                {
                                    _melee = false;
                                    _slot = EnsurceCast(ExampleRoutineSettings.Instance.SingleTargetRangedSlot);
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (ExampleRoutineSettings.Instance.AoeRangedSlot != -1)
                    {
                        _slot = EnsurceCast(ExampleRoutineSettings.Instance.AoeRangedSlot);
                    }
                    if (_slot == -1)
                    {
                        if (ExampleRoutineSettings.Instance.SingleTargetRangedSlot != -1)
                        {
                            _slot = EnsurceCast(ExampleRoutineSettings.Instance.SingleTargetRangedSlot);
                        }
                        if (_slot == -1)
                        {
                            if (ExampleRoutineSettings.Instance.AoeMeleeSlot != -1)
                            {
                                _melee = true;
                                _slot = EnsurceCast(ExampleRoutineSettings.Instance.AoeMeleeSlot);
                            }
                            if (_slot == -1)
                            {
                                if (ExampleRoutineSettings.Instance.SingleTargetMeleeSlot != -1)
                                {
                                    _melee = true;
                                    _slot = EnsurceCast(ExampleRoutineSettings.Instance.SingleTargetMeleeSlot);
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                if (melee)
                {
                    if (ExampleRoutineSettings.Instance.SingleTargetMeleeSlot != -1)
                    {
                        _slot = EnsurceCast(ExampleRoutineSettings.Instance.SingleTargetMeleeSlot);
                    }
                    if (_slot == -1)
                    {
                        if (ExampleRoutineSettings.Instance.AoeMeleeSlot != -1)
                        {
                            _slot = EnsurceCast(ExampleRoutineSettings.Instance.AoeMeleeSlot);
                        }
                        if (_slot == -1)
                        {
                            if (ExampleRoutineSettings.Instance.SingleTargetRangedSlot != -1)
                            {
                                _melee = false;
                                _slot = EnsurceCast(ExampleRoutineSettings.Instance.SingleTargetRangedSlot);
                            }
                            if (_slot == -1)
                            {
                                if (ExampleRoutineSettings.Instance.AoeRangedSlot != -1)
                                {
                                    _melee = false;
                                    _slot = EnsurceCast(ExampleRoutineSettings.Instance.AoeRangedSlot);
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (ExampleRoutineSettings.Instance.SingleTargetRangedSlot != -1)
                    {
                        _slot = EnsurceCast(ExampleRoutineSettings.Instance.SingleTargetRangedSlot);
                    }
                    if (_slot == -1)
                    {
                        if (ExampleRoutineSettings.Instance.AoeRangedSlot != -1)
                        {
                            _slot = EnsurceCast(ExampleRoutineSettings.Instance.AoeRangedSlot);
                        }
                        if (_slot == -1)
                        {
                            if (ExampleRoutineSettings.Instance.SingleTargetMeleeSlot != -1)
                            {
                                _melee = true;
                                _slot = EnsurceCast(ExampleRoutineSettings.Instance.SingleTargetMeleeSlot);
                            }
                            if (_slot == -1)
                            {
                                if (ExampleRoutineSettings.Instance.AoeMeleeSlot != -1)
                                {
                                    _melee = true;
                                    _slot = EnsurceCast(ExampleRoutineSettings.Instance.AoeMeleeSlot);
                                }
                            }
                        }
                    }
                }
            }
            return new KeyValuePair<int, bool>(_slot, _melee);
        }

        // This stufs under here create a Dictionary of all the Routine settings and let all other component of the bot request info or modify the settings trught the message system.
        // http://stackoverflow.com/a/824854
        private void RegisterExposedSettings()
        {
            if (_exposedSettings != null)
                return;

            _exposedSettings = new Dictionary<string, Func<Tuple<object, string>[], object>>();

            // Not a part of settings, so do it manually
            _exposedSettings.Add("SetLeash", param =>
            {
                _currentLeashRange = (int)param[0].Item1;
                return null;
            });

            _exposedSettings.Add("GetLeash", param =>
            {
                return _currentLeashRange;
            });

            // Automatically handle all settings

            PropertyInfo[] properties = typeof(ExampleRoutineSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo p in properties)
            {
                // Only work with ints
                if (p.PropertyType != typeof(int) && p.PropertyType != typeof(bool))
                {
                    continue;
                }

                // If not writable then cannot null it; if not readable then cannot check it's value
                if (!p.CanWrite || !p.CanRead)
                {
                    continue;
                }

                MethodInfo mget = p.GetGetMethod(false);
                MethodInfo mset = p.GetSetMethod(false);

                // Get and set methods have to be public
                if (mget == null)
                {
                    continue;
                }
                if (mset == null)
                {
                    continue;
                }

                Log.InfoFormat("Name: {0} ({1})", p.Name, p.PropertyType);

                _exposedSettings.Add("Set" + p.Name, param =>
                {
                    p.SetValue(ExampleRoutineSettings.Instance, param[0]);
                    return null;
                });

                _exposedSettings.Add("Get" + p.Name, param =>
                {
                    return p.GetValue(ExampleRoutineSettings.Instance);
                });
            }
        }
	}
}
