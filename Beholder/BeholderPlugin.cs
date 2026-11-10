using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Boardgame;
using Boardgame.Board;
using Boardgame.BoardEntities;
using Boardgame.BoardEntities.Abilities;
using Boardgame.Data;
using Boardgame.SerializableEvents;
using DataKeys;
using HarmonyLib;
using UnityEngine;
using Utils;

namespace Beholder;

/// <summary>
/// The Beholder: the Detect Enemies eye as a hostile monster with eye rays after the D&amp;D beholder. It joins each
/// floor's monster deck from the second floor on. Host only; guests need nothing.
/// </summary>
/// <remarks>
/// The eye's piece (EyeOfAvalon) counts as a monster with an AI in every player's game, so the full syncs read the
/// same on guests' machines. Its setup is applied only when the game creates the eye on the monsters' team, so the
/// party's Detect Enemies eyes (Revolutions' Scrying Beacons) are untouched. Guests see the eye's model and its name,
/// Eye of Avalon; its spells are the game's own abilities, played on every machine.
/// </remarks>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class BeholderPlugin : BaseUnityPlugin
{
    internal const string PluginId = "umbra.beholder";
    internal const string PluginName = "Beholder";
    internal const string PluginVersion = "0.1.0";

    private const BoardPieceId EyePiece = BoardPieceId.EyeOfAvalon;

    private static readonly FieldInfo? GameContextField =
        typeof(GameHub).GetField("gameContext", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly AccessTools.FieldRef<EffectSink, int> EffectSinkOwnerRef =
        AccessTools.FieldRefAccess<EffectSink, int>("ownerPieceId");

    // Taken off the eye's copy: the party summon's types (it lights up the board, lasts a few rounds, can't be moved)
    // and fixture types. Creature and a health bar are added.
    private static readonly PieceType[] RemovedTypes =
    {
        PieceType.Player, PieceType.Bot, PieceType.Downable, PieceType.NonAttackable, PieceType.NonGrabbable,
        PieceType.PreserveBetweenLevels, PieceType.UpdateFogOfWar, PieceType.Prop, PieceType.Immovable,
        PieceType.RefillPotionsOnLevelTransition, PieceType.CanReceiveStatBoostPotions,
    };

    private static BeholderPlugin? instance;
    private static AbilityFactory? abilityFactory;
    private static bool planning;

    private readonly Dictionary<GameConfigType, PieceConfigData> copies = new();
    private readonly HashSet<int> live = new();
    private readonly HashSet<int> seen = new();
    private readonly List<Ability> tagged = new();
    private readonly List<Ability> toTag = new();
    private Ability? deathBeam;
    private Ability.AbilityDamage deathBeamOriginalDamage;
    private int deathBeamOriginalCooldown;
    private bool tweaksApplied;
    private bool tweaksPrepared;

    private ConfigEntry<bool> modEnabled = null!;
    private ConfigEntry<int> deckCount = null!;
    private ConfigEntry<int> fromFloor = null!;
    private ConfigEntry<int> health = null!;
    private ConfigEntry<int> armor = null!;
    private ConfigEntry<int> attack = null!;
    private ConfigEntry<int> crit = null!;
    private ConfigEntry<int> move = null!;
    private ConfigEntry<int> vision = null!;
    private ConfigEntry<string> abilities = null!;
    private ConfigEntry<string> behaviours = null!;
    private ConfigEntry<string> cooldownsSetting = null!;
    private ConfigEntry<string> rangedTags = null!;
    private ConfigEntry<int> deathBeamDamage = null!;
    private ConfigEntry<int> deathBeamCooldown = null!;
    private ConfigEntry<bool> skipAbilityChance = null!;
    private ConfigEntry<string> castLook = null!;
    private ConfigEntry<string> looks = null!;
    private string? cooldownsParsedFrom;
    private Dictionary<AbilityKey, int> cooldowns = new();
    private Harmony? harmony;
    private bool patchFailed;
    private bool inLevel;
    private float nextCheck;

    private void Awake()
    {
        instance = this;
        modEnabled = Config.Bind("General", "Enabled", true, "The Beholder joins the monsters.");
        deckCount = Config.Bind("Beholder", "DeckCount", 1, new ConfigDescription(
            "How many go into each floor's monster deck. The game draws from the deck as it spawns, so a floor gets at most this many, and sometimes none.",
            new AcceptableValueRange<int>(0, 5)));
        fromFloor = Config.Bind("Beholder", "FromFloor", 2, new ConfigDescription(
            "The first floor whose deck gets it (1 = the entrance floor too).", new AcceptableValueRange<int>(1, 10)));
        health = Config.Bind("Beholder", "Health", 20, "Health at full strength (3 heroes); the game scales monsters by party size.");
        armor = Config.Bind("Beholder", "Armor", 1, "Armor.");
        attack = Config.Bind("Beholder", "Attack", 4, "Base attack damage (it has no bite; some spells scale from it).");
        crit = Config.Bind("Beholder", "CritDamage", 6, "Damage of a critical hit.");
        move = Config.Bind("Beholder", "Move", 3, "Tiles it can move in a turn.");
        vision = Config.Bind("Beholder", "Vision", 20, "How far it sees.");
        abilities = Config.Bind("Beholder", "Abilities", "DeathBeam, DrainingKiss, Shockwave, EnemyFrostball, LightningBolt, EnemyFlashbang",
            "Its eye rays, the game's ability names. After the D&D beholder: DeathBeam (disintegration/death, the Mad Elven King's beam), DrainingKiss " +
            "(enervation), Shockwave (telekinetic, the Elven Queen's blast), EnemyFrostball (paralysis), EnemyFlashbang (fear), plus LightningBolt. No bite.");
        behaviours = Config.Bind("Beholder", "Behaviours", "Patrol, RangedAttackHighPrio, FollowPlayerRangedAttacker",
            "Its AI behaviours, the game's names: it wanders, fires its rays first, and keeps at range like an archer.");
        cooldownsSetting = Config.Bind("Beholder", "Cooldowns", "DeathBeam: 3, Shockwave: 3, EnemyFrostball: 2, LightningBolt: 2, DrainingKiss: 2, EnemyFlashbang: 2",
            "Its own cooldowns, in its turns: 'Ability: turns' (0 = none). Any ability not listed gets 1, so no ray is used twice in one turn. " +
            "Only the beholder is affected: abilities are shared with other monsters.");
        rangedTags = Config.Bind("Beholder", "RangedTags", "DeathBeam, EnemyFlashbang",
            "Rays tagged as ranged attacks while a beholder is on the board, so its AI fires them (DeathBeam is tagged a spell only in the game). " +
            "Abilities are shared, so the tag comes off when the last beholder goes.");
        deathBeamDamage = Config.Bind("Beholder", "DeathBeamDamage", 5,
            "DeathBeam's damage while a beholder is on the board, at full strength (the game scales it by party size). 0 keeps the game's own. " +
            "Shared with the Mad Elven King, and put back when the last beholder goes.");
        deathBeamCooldown = Config.Bind("Beholder", "DeathBeamCooldown", -1,
            "DeathBeam's own cooldown while a beholder is on the board. -1 keeps the game's own (the beholder's Cooldowns setting already limits it).");
        skipAbilityChance = Config.Bind("Beholder", "SkipAbilityChance", true,
            "True: its ranged planning skips the game's per-ability chance roll, so it uses a ray whenever one is ready and in range.");
        castLook = Config.Bind("Looks", "CastLook", "BossHandHealState",
            "A look shown while it casts: the game's add-effect event just before each ray and remove-effect just after, so every player sees it. " +
            "BossHandHealState is the Mad King's hands' healing aura; it does nothing on the eye (only the Mad King reacts to it, on his own floor, " +
            "where no cast look is shown). Empty = none.");
        looks = Config.Bind("Looks", "Looks", string.Empty,
            "Looks worn all the time, comma-separated status effect names. Empty = none. Use only effects that change nothing in play, such as AbilityBuildUp or ChargeUp.");

        harmony = new Harmony(PluginId);
        try
        {
            harmony.Patch(AccessTools.Method(typeof(GameStartup), "InitializeGame"),
                postfix: new HarmonyMethod(typeof(BeholderPlugin), nameof(InitializeGamePostfix)));
            harmony.Patch(AccessTools.Method(typeof(Piece), nameof(Piece.CreatePiece)),
                prefix: new HarmonyMethod(typeof(BeholderPlugin), nameof(CreatePiecePrefix)));
            harmony.Patch(AccessTools.Method(typeof(AIDirectorDeckConstructor), nameof(AIDirectorDeckConstructor.ConstructMonsterDeck)),
                postfix: new HarmonyMethod(typeof(BeholderPlugin), nameof(ConstructMonsterDeckPostfix)));
            harmony.Patch(AccessTools.Method(typeof(EffectSink), nameof(EffectSink.SetAbilityCountdown)),
                prefix: new HarmonyMethod(typeof(BeholderPlugin), nameof(SetAbilityCountdownPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Boardgame.BoardEntities.AI.Behaviour), "CanUseAbility",
                    new[] { typeof(Piece), typeof(Ability), typeof(Boardgame.BoardEntities.AI.AILogger) }),
                prefix: new HarmonyMethod(typeof(BeholderPlugin), nameof(CanUseAbilityPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Boardgame.BoardEntities.AI.PieceAI), nameof(Boardgame.BoardEntities.AI.PieceAI.CreatePlan)),
                postfix: new HarmonyMethod(typeof(BeholderPlugin), nameof(CastLookPostfix)));
        }
        catch (Exception e)
        {
            patchFailed = true;
            Logger.LogError($"The game could not be patched, so the mod stays off. Look for a newer version. {e}");
            return;
        }

        // Kept apart: a failure here only loses the chance skip.
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(Boardgame.BoardEntities.AI.RangedAttackBehaviour), nameof(Boardgame.BoardEntities.AI.RangedAttackBehaviour.PlanNextAction),
                    new[] { typeof(BoardQuery), typeof(PieceAndTurnController), typeof(BoardModel) }),
                prefix: new HarmonyMethod(typeof(BeholderPlugin), nameof(PlanPrefix)),
                finalizer: new HarmonyMethod(typeof(BeholderPlugin), nameof(PlanFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(Boardgame.BoardEntities.AI.Behaviour), "GetPassesRandomChance", new[] { typeof(Ability) }),
                prefix: new HarmonyMethod(typeof(BeholderPlugin), nameof(ChancePrefix)));
        }
        catch (Exception e)
        {
            Logger.LogError($"The chance-skip patches failed; the beholder keeps the game's chance rolls. {e}");
        }

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded: {(modEnabled.Value ? $"{deckCount.Value} per floor deck from floor {fromFloor.Value}" : "off")}.");
    }

    private void OnDestroy()
    {
        ReleaseTweaks();
        harmony?.UnpatchSelf();
    }

    private static bool Active => instance != null && !instance.patchFailed && instance.modEnabled.Value;

    private static GameContext? Context => GameContextField?.GetValue(null) as GameContext;

    private static bool IsHost(GameContext? context) => context?.networkController != null && context.networkController.IsMasterClient;

    private bool IsOurs(Piece? piece) => piece != null && copies.Values.Any(copy => ReferenceEquals(copy, piece.GetPieceConfig()));

    /// <summary>Whether the game has a model for a piece (a piece without one freezes the game on its turn).</summary>
    private static bool HasModel(BoardPieceId pieceId)
    {
        var key = pieceId.ToString().ToLowerInvariant();
        foreach (var locator in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
        {
            if ((locator.Locate(key, typeof(GameObject), out var found) && found != null && found.Count > 0)
                || (locator.Locate(key + "LQ", typeof(GameObject), out var low) && low != null && low.Count > 0))
            {
                return true;
            }
        }

        return false;
    }

    private void Update()
    {
        try
        {
            var context = Context;
            var playing = context != null && !GameHub.NoActiveLevel && context.pieceAndTurnController != null && context.gameStateMachine != null
                && context.gameStateMachine.GetCurrentGameState() == GameStates.PlayingState;
            if (!playing)
            {
                if (inLevel)
                {
                    inLevel = false;
                    seen.Clear();
                    live.Clear();
                    ReleaseTweaks();
                }

                return;
            }

            inLevel = true;
            if (!Active || !IsHost(context) || Time.unscaledTime < nextCheck)
            {
                return;
            }

            nextCheck = Time.unscaledTime + 0.5f;
            Follow(context!);
        }
        catch (Exception e)
        {
            Logger.LogError($"Update failed: {e}");
        }
    }

    /// <summary>Follows the beholders on the board: the shared-ability tweaks while one lives, and its always-on looks.</summary>
    private void Follow(GameContext context)
    {
        live.Clear();
        foreach (var piece in context.pieceAndTurnController.Pieces(PieceAndTurnController.SearchPiece.TurnQueue).ToList())
        {
            if (piece == null || piece.IsDead() || !IsOurs(piece))
            {
                continue;
            }

            live.Add(piece.networkID);
            if (seen.Add(piece.networkID))
            {
                Logger.LogInfo($"Beholder #{piece.networkID} is on the board at {piece.gridPos.min}: HP {piece.GetHealth()}/{piece.GetMaxHealth()}, " +
                    $"AI={(piece.pieceAI != null)}, player={piece.IsPlayer()}.");
            }
        }

        if (live.Count > 0)
        {
            ApplyTweaks();
            KeepLooks(context);
        }
        else
        {
            ReleaseTweaks();
        }
    }

    private void KeepLooks(GameContext context)
    {
        var wanted = ParseNames<EffectStateType>(looks.Value, "looks");
        if (wanted.Length == 0)
        {
            return;
        }

        var changed = false;
        foreach (var id in live)
        {
            if (!context.pieceAndTurnController.TryGetPiece(id, out var piece) || piece == null)
            {
                continue;
            }

            foreach (var look in wanted)
            {
                if (!piece.HasEffectState(look))
                {
                    piece.effectSink.AddStatusEffect(look, 99);
                    changed = true;
                }
                else if (piece.effectSink.GetEffectStateDurationTurnsLeft(look) < 10)
                {
                    piece.effectSink.SetStatusEffectDuration(look, 99);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            context.serializableEventQueue.SendResponseEvent(SerializableEvent.CreateRecovery());
        }
    }

    /// <summary>Looks the beholder's rays up once, so DeathBeam and the ranged tags can be adjusted while one is on the board.</summary>
    private void PrepareTweaks(AbilityKey[] rays)
    {
        if (tweaksPrepared || abilityFactory == null)
        {
            return;
        }

        tweaksPrepared = true;
        var tagKeys = new HashSet<AbilityKey>(ParseNames<AbilityKey>(rangedTags.Value, "ranged tags"));
        abilityFactory.LoadAbilities(rays).OnKeysLoaded(loaded =>
        {
            foreach (var pair in loaded)
            {
                if (pair.Value == null)
                {
                    continue;
                }

                if (pair.Key == AbilityKey.DeathBeam)
                {
                    deathBeam = pair.Value;
                    deathBeamOriginalDamage = pair.Value.abilityDamage;
                    deathBeamOriginalCooldown = pair.Value.cooldown;
                }

                if (tagKeys.Contains(pair.Key))
                {
                    toTag.Add(pair.Value);
                }
            }

            if (tweaksApplied)
            {
                tweaksApplied = false;
                ApplyTweaks();
            }
        });
    }

    private void ApplyTweaks()
    {
        if (tweaksApplied)
        {
            return;
        }

        tweaksApplied = true;
        if (deathBeam != null)
        {
            if (deathBeamDamage.Value > 0)
            {
                var damage = deathBeam.abilityDamage;
                damage.targetDamage = deathBeamDamage.Value;
                damage.splashDamage = deathBeamDamage.Value;
                damage.critDamage = deathBeamDamage.Value + 2;
                damage.critSplashDamage = deathBeamDamage.Value + 2;
                deathBeam.abilityDamage = damage;
            }

            if (deathBeamCooldown.Value >= 0)
            {
                deathBeam.cooldown = deathBeamCooldown.Value;
            }
        }

        foreach (var ability in toTag)
        {
            var current = ability.abilityAttributes ?? Array.Empty<Ability.AbilityAttribute>();
            if (current.Contains(Ability.AbilityAttribute.RangedAttack) || tagged.Contains(ability))
            {
                continue;
            }

            ability.abilityAttributes = current.Append(Ability.AbilityAttribute.RangedAttack).ToArray();
            tagged.Add(ability);
        }

        Logger.LogInfo($"Beholder on the board: DeathBeam damage {(deathBeam != null ? deathBeam.abilityDamage.targetDamage.ToString() : "?")}, " +
            $"{tagged.Count} rays tagged as ranged attacks.");
    }

    private void ReleaseTweaks()
    {
        if (!tweaksApplied)
        {
            return;
        }

        tweaksApplied = false;
        if (deathBeam != null)
        {
            deathBeam.abilityDamage = deathBeamOriginalDamage;
            deathBeam.cooldown = deathBeamOriginalCooldown;
        }

        foreach (var ability in tagged)
        {
            ability.abilityAttributes = (ability.abilityAttributes ?? Array.Empty<Ability.AbilityAttribute>())
                .Where(attribute => attribute != Ability.AbilityAttribute.RangedAttack).ToArray();
        }

        tagged.Clear();
        Logger.LogInfo("No beholder on the board: DeathBeam's own values and the rays' own tags put back.");
    }

    private static void InitializeGamePostfix(GameStartup __instance)
    {
        try
        {
            abilityFactory = Traverse.Create(__instance).Field<AbilityFactory>("abilityFactory").Value;
        }
        catch (Exception e)
        {
            instance?.Logger.LogWarning($"The game's ability loader could not be read: {e.Message}");
        }
    }

    /// <summary>Gives the eye its beholder setup as the game creates it on the monsters' team. The party's eyes are untouched.</summary>
    private static void CreatePiecePrefix(ref PieceConfigData config, BoardPieceId pieceType, Team team)
    {
        if (!Active || pieceType != EyePiece || team != Team.Two || !IsHost(Context))
        {
            return;
        }

        try
        {
            var copy = instance!.SetupFor(Context!);
            if (copy != null)
            {
                config = copy;
                instance.PrepareTweaks(copy.Abilities);
            }
        }
        catch (Exception e)
        {
            instance!.Logger.LogError($"Setting up the beholder failed; the eye keeps the game's own setup. {e}");
        }
    }

    private PieceConfigData? SetupFor(GameContext context)
    {
        var configType = MotherbrainGlobalVars.CurrentConfig;
        if (copies.TryGetValue(configType, out var existing) && existing != null)
        {
            return existing;
        }

        var baseConfig = context.gameDataAPI.GetPieceConfig(EyePiece, configType);
        if (baseConfig == null)
        {
            Logger.LogWarning($"No setup found for the eye in {configType}.");
            return null;
        }

        var copy = UnityEngine.Object.Instantiate(baseConfig);
        copy.name = "Beholder";
        var types = (baseConfig.PieceType ?? Array.Empty<PieceType>()).Where(type => !RemovedTypes.Contains(type)).ToList();
        foreach (var added in new[] { PieceType.Creature, PieceType.ShowHealthbar })
        {
            if (!types.Contains(added))
            {
                types.Add(added);
            }
        }

        copy.PieceType = types.ToArray();
        copy.AliveForRounds = 0; // the Detect Enemies eye is a summon that fades after a few rounds
        copy.UseWhenCreated = Array.Empty<AbilityKey>();
        copy.UseWhenKilled = Array.Empty<AbilityKey>();
        copy.ApplyEffectOnHit = EffectStateType.Undefined; // Revolutions' Scrying Beacon charms whoever hits it
        copy.InnateCounterDamage = 0;
        copy.InnateCounterDamageExtraDamage = 0;
        copy.ActionPoint = 2;
        copy.StartHealth = Math.Max(1, health.Value);
        copy.StartArmor = Math.Max(0, armor.Value);
        copy.AttackDamage = Math.Max(0, attack.Value);
        copy.CriticalHitDamage = Math.Max(0, crit.Value);
        copy.MoveRange = Math.Max(0, move.Value);
        copy.VisionRange = Math.Max(1, vision.Value);
        var rays = ParseNames<AbilityKey>(abilities.Value, "abilities");
        var plans = ParseNames<DataKeys.Behaviour>(behaviours.Value, "behaviours");
        if (rays.Length > 0)
        {
            copy.Abilities = rays;
        }

        copy.Behaviours = plans.Length > 0 ? plans : new[] { DataKeys.Behaviour.Patrol, DataKeys.Behaviour.RangedAttackHighPrio };
        if (abilityFactory != null)
        {
            copy.AbilityPromises = abilityFactory.LoadAbilities(copy.Abilities);
        }

        copies[configType] = copy;
        Logger.LogInfo($"Beholder set up for {configType}: rays [{string.Join(", ", copy.Abilities)}], behaviours [{string.Join(", ", copy.Behaviours)}], " +
            $"HP {copy.StartHealth}, armor {copy.StartArmor}, move {copy.MoveRange}, types [{string.Join(", ", copy.PieceType)}], " +
            $"immune to [{string.Join(", ", copy.ImmuneToStatusEffects ?? Array.Empty<EffectStateType>())}].");
        return copy;
    }

    /// <summary>Adds the beholder to the floor's monster deck, after the game (or Revolutions) has built it.</summary>
    private static void ConstructMonsterDeckPostfix(MonsterDeck __result, int floorIndex, IRnd rng)
    {
        if (!Active || __result?.monsterDeckStandard == null || !IsHost(Context))
        {
            return;
        }

        try
        {
            var self = instance!;
            if (self.deckCount.Value <= 0 || floorIndex < self.fromFloor.Value)
            {
                return;
            }

            if (!HasModel(EyePiece))
            {
                self.Logger.LogWarning("The eye has no model in this game; the beholder is left out of the deck.");
                return;
            }

            for (var i = 0; i < self.deckCount.Value; i++)
            {
                var entry = new MonsterDeck.MonsterDeckEntry { BoardPieceId = EyePiece, enemyWeight = EnemyWeight.Light, isRedrawEnabled = false };
                var at = __result.monsterDeckStandard.Count == 0 ? 0 : rng.Range(0, __result.monsterDeckStandard.Count + 1);
                __result.monsterDeckStandard.Insert(at, entry);
            }

            self.Logger.LogInfo($"Floor deck {floorIndex}: Beholder x{self.deckCount.Value} added among {__result.monsterDeckStandard.Count} entries.");
        }
        catch (Exception e)
        {
            instance!.Logger.LogError($"Adding the beholder to the monster deck failed: {e}");
        }
    }

    private bool TryGetCooldown(AbilityKey ability, out int turns)
    {
        turns = 0;
        if (ability == AbilityKey.EnemyMelee || ability == AbilityKey.PlayerMelee)
        {
            return false;
        }

        if (cooldownsParsedFrom != cooldownsSetting.Value)
        {
            cooldownsParsedFrom = cooldownsSetting.Value;
            cooldowns = new Dictionary<AbilityKey, int>();
            foreach (var part in (cooldownsSetting.Value ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split(':');
                if (pieces.Length == 2 && int.TryParse(pieces[1].Trim(), out var value)
                    && Enum.TryParse<AbilityKey>(pieces[0].Trim(), true, out var key) && Enum.IsDefined(typeof(AbilityKey), key))
                {
                    cooldowns[key] = value;
                }
                else
                {
                    Logger.LogWarning($"Cooldowns: '{part.Trim()}' ignored; expected 'Ability: turns'.");
                }
            }
        }

        turns = cooldowns.TryGetValue(ability, out var configured) ? configured : 1;
        return true;
    }

    /// <summary>When the beholder fires a ray, the countdown stored on it comes from its Cooldowns setting (N is stored as N - 1).</summary>
    private static void SetAbilityCountdownPrefix(EffectSink __instance, AbilityKey ability, ref int countdown)
    {
        if (!Active)
        {
            return;
        }

        try
        {
            var context = Context;
            if (context?.pieceAndTurnController == null || !IsHost(context)
                || !context.pieceAndTurnController.TryGetPiece(EffectSinkOwnerRef(__instance), out var piece) || !instance!.IsOurs(piece)
                || !instance.TryGetCooldown(ability, out var turns))
            {
                return;
            }

            countdown = turns <= 0 ? -1 : Math.Max(countdown, turns - 1);
        }
        catch (Exception e)
        {
            instance!.Logger.LogWarning($"Cooldown for {ability} not applied: {e.Message}");
        }
    }

    /// <summary>The AI treats a ray as not ready while its countdown on the beholder is running.</summary>
    private static bool CanUseAbilityPrefix(Piece p, Ability ability, ref bool __result)
    {
        if (!Active || p == null || ability == null || !instance!.IsOurs(p)
            || !instance.TryGetCooldown(ability.abilityKey, out var turns) || turns <= 0 || p.GetCooldownForAbility(ability.abilityKey) < 0)
        {
            return true;
        }

        __result = false;
        return false;
    }

    /// <summary>The cast look: the game's add-effect and remove-effect events around each ray in its plan.</summary>
    private static void CastLookPostfix(Piece piece, ref SerializableEvent[] __result)
    {
        if (!Active || __result == null || __result.Length == 0 || piece == null || !instance!.IsOurs(piece) || !IsHost(Context))
        {
            return;
        }

        try
        {
            var name = instance.castLook.Value?.Trim() ?? string.Empty;
            if (name.Length == 0 || char.IsDigit(name[0]) || !Enum.TryParse<EffectStateType>(name, true, out var look) || !Enum.IsDefined(typeof(EffectStateType), look))
            {
                return;
            }

            // The Mad King reacts to his hands' healing aura on his own floor; no cast look there.
            if (look == EffectStateType.BossHandHealState && Context!.pieceAndTurnController.Pieces().Any(p => p != null && p.boardPieceId == BoardPieceId.BossTown))
            {
                return;
            }

            var events = new List<SerializableEvent>(__result);
            var casts = 0;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i] is not SerializableEventUseAbility use || use.pieceId != piece.networkID)
                {
                    continue;
                }

                events.Insert(i, SerializableEvent.CreateAddEffectState(piece.networkID, look, 1));
                events.Insert(i + 2, SerializableEvent.CreateRemoveEffectState(piece.networkID, look));
                i += 2;
                casts++;
            }

            if (casts > 0)
            {
                __result = events.ToArray();
            }
        }
        catch (Exception e)
        {
            instance!.Logger.LogError($"The beholder's cast look failed; the turn plays without it. {e}");
        }
    }

    private static void PlanPrefix(BoardQuery boardQuery)
    {
        planning = Active && instance!.skipAbilityChance.Value && boardQuery?.piece != null && instance.live.Contains(boardQuery.PieceId);
    }

    private static Exception? PlanFinalizer(Exception? __exception)
    {
        planning = false;
        return __exception;
    }

    private static bool ChancePrefix(ref bool __result)
    {
        if (!planning)
        {
            return true;
        }

        __result = true;
        return false;
    }

    private T[] ParseNames<T>(string list, string what)
        where T : struct
    {
        var result = new List<T>();
        foreach (var raw in (list ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (char.IsDigit(name[0]) || !Enum.TryParse<T>(name, true, out var value) || !Enum.IsDefined(typeof(T), value))
            {
                Logger.LogWarning($"{what}: '{name}' is not a name the game knows; left out.");
                continue;
            }

            if (!result.Contains(value))
            {
                result.Add(value);
            }
        }

        return result.ToArray();
    }
}
