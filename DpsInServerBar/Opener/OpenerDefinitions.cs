using System.Collections.Generic;
using System.Linq;

namespace DpsInServerBar.Opener;

public enum StepKind
{
    Gcd,
    Ogcd,

    /// <summary>A tincture: any item used counts.</summary>
    Potion,
}

/// <summary>
/// One button of an opener. <paramref name="Names"/>: English action names, any of them counts (e.g. "Aetherflow" or
/// "Dissipation"); empty for a tincture. <paramref name="Prepull"/>: seconds before the pull (e.g. 3 for "-3").
/// <paramref name="Stat"/>: tincture stat (Strength, Dexterity, Intelligence, Mind), for its icon.
/// </summary>
public sealed record OpenerStep(IReadOnlyList<string> Names, StepKind Kind, double? Prepull = null, string? Stat = null)
{
    public bool IsGcd => Kind == StepKind.Gcd;
}

/// <summary>
/// An opener of a job. <paramref name="Ignored"/>: actions that never count as mistakes when they are not the expected
/// step (e.g. the Ninja mudras typed for a Ninjutsu shown as a single box).
/// </summary>
public sealed record OpenerDefinition(
    string Id, string Job, string Name, string Source, IReadOnlyList<OpenerStep> Steps,
    IReadOnlyList<string> Notes, IReadOnlyList<string>? Ignored = null);

/// <summary>
/// Built-in openers, from The Balance (thebalanceffxiv.com) job guides, level 100. Actions are matched by their English
/// name as reported by the game, so combo and buff replacements (Scarlet Delirium, Drakesbane...) are named as such.
/// </summary>
public static class OpenerDefinitions
{
    private static OpenerStep G(string names, double? prepull = null) => new(names.Split('|'), StepKind.Gcd, prepull);
    private static OpenerStep O(string names, double? prepull = null) => new(names.Split('|'), StepKind.Ogcd, prepull);
    private static OpenerStep Pot(string stat, double? prepull = null) => new([], StepKind.Potion, prepull, stat);

    private const string Str = "Strength", Dex = "Dexterity", Int = "Intelligence", Mnd = "Mind";
    private const string Balance = "The Balance";

    // ------------------------------------------------------------------ Tanks

    private static readonly OpenerDefinition[] Tanks =
    [
        new("pld-standard", "PLD", "Standard opener", $"{Balance}, Dawntrail 7.0",
        [
            G("Holy Spirit", 1.75),
            G("Fast Blade"), G("Riot Blade"), Pot(Str), G("Royal Authority"), O("Fight or Flight"), O("Imperator"),
            G("Confiteor"), O("Circle of Scorn"), O("Expiacion"), G("Blade of Faith"), O("Intervene"), G("Blade of Truth"),
            O("Intervene"), G("Blade of Valor"), O("Blade of Honor"), G("Goring Blade"), G("Atonement"), G("Supplication"),
            G("Sepulchre"), G("Holy Spirit"),
        ],
        [
            "Holy Spirit is hard cast before the pull (about -1.75 s) so it lands as the boss is pulled.",
            "At 2.50 GCD, double weave Imperator right after Fight or Flight.",
        ]),

        new("war-all-around", "WAR", "All-Around opener", $"{Balance}, Dawntrail 7.2",
        [
            G("Tomahawk", 0.7),
            O("Infuriate"), G("Heavy Swing"), G("Maim"), G("Storm's Eye"), O("Inner Release"), Pot(Str), G("Inner Chaos"),
            O("Upheaval"), O("Onslaught"), G("Fell Cleave"), O("Onslaught"), G("Fell Cleave"), O("Onslaught"),
            G("Fell Cleave"), O("Primal Wrath"), O("Infuriate"), G("Primal Rend"), G("Primal Ruination"), G("Inner Chaos"),
            G("Heavy Swing"), G("Maim"), G("Storm's Path"), G("Fell Cleave"), O("Infuriate"), G("Inner Chaos"),
        ],
        [
            "Tomahawk at -0.7 s, the pull at 0. Party buffs are expected around 7.8 s, with the first Inner Chaos.",
            "Upheaval and the Onslaughts are used late to catch the party buffs.",
            "The order of the Inner Release GCDs can be swapped as you like.",
            "Made for 2.47 or slower; at 2.45 and faster the oGCDs may move slightly.",
        ]),

        new("drk-standard", "DRK", "Standard opener", $"{Balance}, Dawntrail 7.2",
        [
            O("The Blackest Night", 3), G("Unmend", 1), Pot(Str, 1),
            O("Edge of Shadow"), G("Hard Slash"), O("Living Shadow"), G("Syphon Strike"), G("Souleater"), O("Delirium"),
            G("Disesteem"), O("Salted Earth"), O("Edge of Shadow"), G("Scarlet Delirium"), O("Shadowbringer"),
            O("Edge of Shadow"), G("Comeuppance"), O("Carve and Spit"), O("Edge of Shadow"), G("Torcleaver"),
            O("Shadowbringer"), O("Edge of Shadow"), G("Bloodspiller"), O("Salt and Darkness"),
        ],
        [
            "If the pre-pull The Blackest Night will not break, leave it out: the last Edge of Shadow comes a little later, as soon as you have the MP.",
            "Shadowstride or Hard Slash can replace Unmend as the pull, with an extra Hard Slash before Disesteem.",
            "If the first Edge of Shadow would clip because the boss is far, use it after Hard Slash instead.",
        ]),

        new("gnb-general", "GNB", "General opener", $"{Balance}, Dawntrail 7.4",
        [
            G("Lightning Shot", 0.7),
            O("Bloodfest"), G("Keen Edge"), G("Brutal Shell"), O("No Mercy"), Pot(Str), G("Gnashing Fang"), O("Jugular Rip"),
            O("Bow Shock"), G("Double Down"), O("Blasting Zone"), G("Sonic Break"), G("Savage Claw"), O("Abdomen Tear"),
            G("Wicked Talon"), O("Eye Gouge"), G("Reign of Beasts"), G("Noble Blood"), G("Lion Heart"), G("Solid Barrel"),
            G("Gnashing Fang"), O("Jugular Rip"),
        ],
        [
            "Made for 2.50 but usable at any speed; it matches party buffs at about 6.8 s.",
            "Any GCD speed that fits 9 GCDs in No Mercy should late weave No Mercy.",
            "Pull with Lightning Shot (threat, buff alignment). Trajectory at -0.66 s or Provoke at -0.6 s also work.",
            "No Mercy can be used earlier or later to fit phases or your party's buffs.",
        ]),
    ];

    // ------------------------------------------------------------------ Healers

    private static readonly OpenerDefinition[] Healers =
    [
        new("whm-early-refresh", "WHM", "Standard opener (early DoT refresh)", $"{Balance}, Dawntrail 7.4",
        [
            G("Glare III", 2.3),
            Pot(Mnd), G("Dia"), G("Glare III"), G("Glare III"), O("Presence of Mind"), G("Glare IV"), O("Assize"),
            G("Afflatus Misery"), G("Glare IV"), G("Glare IV"), G("Glare III"), G("Glare III"), G("Glare III"),
            G("Glare III"), G("Glare III"), G("Dia"),
        ],
        [
            "The one to use outside of specific cases. Glare IV and Afflatus Misery can move within the window, as long as it ends with Dia.",
            "Use your first Afflatus Rapture when you need healing after the tincture, before the 1 minute window.",
        ]),

        new("whm-double-misery", "WHM", "Double Misery opener", $"{Balance}, Dawntrail 7.4",
        [
            G("Glare III", 2.3),
            Pot(Mnd), G("Dia"), G("Glare III"), G("Glare III"), O("Presence of Mind"), G("Glare IV"), O("Assize"),
            G("Afflatus Misery"), G("Glare IV"), G("Glare IV"), G("Afflatus Rapture"), G("Afflatus Rapture"),
            G("Afflatus Rapture"), G("Afflatus Misery"), G("Glare III"), G("Dia"),
        ],
        [
            "Only for specific cases: two targets from the start, or a lot of healing in the first 30 seconds.",
            "Afflatus Raptures and Glare IVs can move, but no Afflatus Rapture before the first Afflatus Misery, and end with Dia.",
        ]),

        new("sch-early-refresh", "SCH", "Early refresh, 2nd GCD Chain", $"{Balance}, Dawntrail 7.0",
        [
            G("Broil IV", 1.5),
            Pot(Mnd), G("Biolysis"), O("Aetherflow|Dissipation"), G("Broil IV"), O("Chain Stratagem"), G("Broil IV"),
            O("Energy Drain"), G("Broil IV"), O("Energy Drain"), G("Broil IV"), O("Energy Drain"), G("Broil IV"),
            O("Aetherflow|Dissipation"), G("Broil IV"), O("Baneful Impaction"), G("Broil IV"), O("Energy Drain"),
            G("Broil IV"), O("Energy Drain"), G("Broil IV"), O("Energy Drain"), G("Biolysis"),
        ],
        [
            "Aetherflow and Dissipation can be in either order (Dissipation first brings the fairy back earlier).",
            "The ability usage is what you do on a dummy: use a heal or keep an Aetherflow stack if the fight needs it.",
            "Refreshing Biolysis at the end of raid buffs is a gain with at least one other raid buff.",
        ]),

        new("ast-dawntrail", "AST", "Dawntrail opener", $"{Balance}, Dawntrail 7.01",
        [
            O("Earthly Star", 4), G("Fall Malefic", 2.1),
            Pot(Mnd), G("Combust III"), O("Lightspeed"), G("Fall Malefic"), G("Fall Malefic"), O("Divination"),
            O("the Balance"), G("Fall Malefic"), O("Lord of Crowns"), O("Umbral Draw"), G("Fall Malefic"), O("the Spear"),
            O("Oracle"), G("Fall Malefic"), G("Fall Malefic"), G("Fall Malefic"), G("Fall Malefic"), G("Fall Malefic"),
            G("Combust III"), G("Fall Malefic"),
        ],
        [
            "Fall Malefic at -2.1 s: a cast pulls the boss when it lands, and Malefic takes long to land.",
            "Earthly Star can be anywhere from -4 s to about -19 s.",
            "In a party, don't card yourself: the Balance and the Spear go on teammates.",
            "Lightspeed can go anywhere after the first Combust, up to a late weave after the next Fall Malefic.",
        ]),

        new("sge-toxikon", "SGE", "Toxikon opener", $"{Balance}, Dawntrail 7.0",
        [
            G("Eukrasia", 5), Pot(Mnd, 2.1), G("Toxikon II", 1.5),
            G("Eukrasian Dosis III"), G("Dosis III"), G("Dosis III"), G("Dosis III"), G("Phlegma III"), O("Psyche"),
            G("Phlegma III"), G("Dosis III"), G("Dosis III"), G("Dosis III"), G("Dosis III"), G("Eukrasia"),
            G("Eukrasian Dosis III"), G("Dosis III"), G("Dosis III"), G("Dosis III"),
        ],
        []),

        new("sge-pneuma", "SGE", "Pneuma opener", $"{Balance}, Dawntrail 7.0",
        [
            G("Eukrasia", 5), Pot(Mnd, 2.1), G("Pneuma", 1.5),
            G("Eukrasian Dosis III"), G("Dosis III"), G("Dosis III"), G("Dosis III"), G("Phlegma III"), O("Psyche"),
            G("Phlegma III"), G("Dosis III"), G("Dosis III"), G("Dosis III"), G("Dosis III"), G("Eukrasia"),
            G("Eukrasian Dosis III"), G("Dosis III"), G("Dosis III"), G("Dosis III"),
        ],
        []),
    ];

    // ------------------------------------------------------------------ Melee

    private const string MonkPick =
        "5 s or 7 s: pick what fits your party's buffs. Dragon Kick first is the default; Demolish first depends on buff timings or phasing.";

    private const string MonkLunar = "Solar Lunar or Double Lunar depends on the kill time: use Solar Lunar if it ends the fight in an odd Riddle of Fire window.";

    private static readonly OpenerDefinition[] Melee =
    [
        new("mnk-sl-dk-5", "MNK", "Solar Lunar, Dragon Kick first (5 s buffs)", $"{Balance}, Dawntrail 7.0",
        [
            G("Dragon Kick"), O("Perfect Balance"), G("Twin Snakes"), Pot(Str), G("Demolish"), O("Brotherhood"),
            O("Riddle of Fire"), G("Leaping Opo"), O("the Forbidden Chakra"), O("Riddle of Wind"), G("Rising Phoenix"),
            G("Dragon Kick"), G("Wind's Reply"), G("Fire's Reply"), G("Leaping Opo"), O("Perfect Balance"),
            G("Dragon Kick"), G("Leaping Opo"), G("Dragon Kick"), G("Elixir Burst"), G("Leaping Opo"),
        ],
        [MonkPick, MonkLunar]),

        new("mnk-sl-dk-7", "MNK", "Solar Lunar, Dragon Kick first (7 s buffs)", $"{Balance}, Dawntrail 7.0",
        [
            G("Dragon Kick"), O("Perfect Balance"), Pot(Str), G("Twin Snakes"), G("Leaping Opo"), G("Demolish"),
            O("Brotherhood"), O("Riddle of Fire"), G("Rising Phoenix"), O("the Forbidden Chakra"), O("Riddle of Wind"),
            G("Dragon Kick"), G("Wind's Reply"), G("Fire's Reply"), G("Leaping Opo"), O("Perfect Balance"),
            G("Dragon Kick"), G("Leaping Opo"), G("Dragon Kick"), G("Elixir Burst"), G("Leaping Opo"), G("Rising Raptor"),
        ],
        ["The tincture can also go after Twin Snakes.", MonkPick, MonkLunar]),

        new("mnk-sl-demo-7", "MNK", "Solar Lunar, Demolish first (7 s buffs)", $"{Balance}, Dawntrail 7.0",
        [
            G("Demolish"), G("Dragon Kick"), O("Perfect Balance"), Pot(Str), G("Twin Snakes"), G("Pouncing Coeurl"),
            O("Brotherhood"), O("Riddle of Fire"), G("Leaping Opo"), O("the Forbidden Chakra"), O("Riddle of Wind"),
            G("Rising Phoenix"), G("Dragon Kick"), G("Wind's Reply"), G("Fire's Reply"), G("Leaping Opo"),
            O("Perfect Balance"), G("Dragon Kick"), G("Leaping Opo"), G("Dragon Kick"), G("Elixir Burst"), G("Leaping Opo"),
        ],
        ["The tincture can also go after Twin Snakes.", MonkPick, MonkLunar]),

        new("mnk-dl-dk-5", "MNK", "Double Lunar, Dragon Kick first (5 s buffs)", $"{Balance}, Dawntrail 7.0",
        [
            G("Dragon Kick"), O("Perfect Balance"), Pot(Str), G("Leaping Opo"), G("Dragon Kick"), O("Brotherhood"),
            O("Riddle of Fire"), G("Leaping Opo"), O("the Forbidden Chakra"), O("Riddle of Wind"), G("Elixir Burst"),
            G("Dragon Kick"), G("Wind's Reply"), G("Fire's Reply"), G("Leaping Opo"), O("Perfect Balance"),
            G("Dragon Kick"), G("Leaping Opo"), G("Dragon Kick"), G("Elixir Burst"), G("Leaping Opo"),
        ],
        ["The tincture can also be used before the pull (about -1.4 s).", MonkPick, MonkLunar]),

        new("mnk-dl-dk-7", "MNK", "Double Lunar, Dragon Kick first (7 s buffs)", $"{Balance}, Dawntrail 7.0",
        [
            Pot(Str, 1.4),
            G("Dragon Kick"), O("Perfect Balance"), O("the Forbidden Chakra"), G("Leaping Opo"), G("Dragon Kick"),
            G("Leaping Opo"), O("Brotherhood"), O("Riddle of Fire"), G("Elixir Burst"), O("Riddle of Wind"),
            G("Dragon Kick"), G("Wind's Reply"), G("Fire's Reply"), G("Leaping Opo"), O("Perfect Balance"),
            G("Dragon Kick"), G("Leaping Opo"), G("Dragon Kick"), G("Elixir Burst"), G("Leaping Opo"), G("Twin Snakes"),
        ],
        ["The tincture can also go after the second Dragon Kick.", MonkPick, MonkLunar]),

        new("mnk-dl-demo-7", "MNK", "Double Lunar, Demolish first (7 s buffs)", $"{Balance}, Dawntrail 7.0",
        [
            G("Demolish"), G("Dragon Kick"), O("Perfect Balance"), Pot(Str), G("Leaping Opo"), G("Dragon Kick"),
            O("Brotherhood"), O("Riddle of Fire"), G("Leaping Opo"), O("the Forbidden Chakra"), O("Riddle of Wind"),
            G("Elixir Burst"), G("Dragon Kick"), G("Wind's Reply"), G("Fire's Reply"), G("Leaping Opo"),
            O("Perfect Balance"), G("Dragon Kick"), G("Leaping Opo"), G("Dragon Kick"), G("Elixir Burst"), G("Leaping Opo"),
        ],
        ["The tincture can also go after Demolish.", MonkPick, MonkLunar]),

        new("drg-standard", "DRG", "Standard opener (no Piercing Talon)", $"{Balance}, Dawntrail 7.1",
        [
            G("True Thrust"), G("Spiral Blow"), O("Lance Charge"), Pot(Str), G("Chaotic Spring"), O("Battle Litany"),
            O("Geirskogul"), G("Wheeling Thrust"), O("High Jump"), O("Life Surge"), G("Drakesbane"),
            O("Dragonfire Dive"), O("Nastrond"), G("Raiden Thrust"), O("Stardiver"), G("Lance Barrage"), O("Starcross"),
            O("Life Surge"), G("Heavens' Thrust"), O("Rise of the Dragon"), O("Mirage Dive"), G("Fang and Claw"),
            G("Drakesbane"), G("Raiden Thrust"), O("Wyrmwind Thrust"),
        ],
        [
            "Battle Litany goes after the DoT; Geirskogul last so all your buffs boost it.",
            "Apart from those, the oGCD order can often be swapped freely.",
        ]),

        new("drg-pt", "DRG", "Piercing Talon on pull", $"{Balance}, Dawntrail 7.1",
        [
            O("Elusive Jump", 15), G("Piercing Talon", 0.85),
            G("True Thrust"), Pot(Str), G("Spiral Blow"), O("Lance Charge"), O("Battle Litany"), G("Chaotic Spring"),
            O("Geirskogul"), G("Wheeling Thrust"), O("High Jump"), O("Life Surge"), G("Drakesbane"),
            O("Dragonfire Dive"), O("Nastrond"), G("Raiden Thrust"), O("Stardiver"), G("Lance Barrage"), O("Starcross"),
            O("Life Surge"), G("Heavens' Thrust"), O("Rise of the Dragon"), O("Mirage Dive"), G("Fang and Claw"),
            G("Drakesbane"), G("Raiden Thrust"), O("Wyrmwind Thrust"),
        ],
        [
            "For bosses you can't reach in melee before the pull: Piercing Talon starts the GCD from range.",
            "Wait a tiny bit before Geirskogul, to leave room for a double weave after it.",
        ]),

        new("nin-4th", "NIN", "Standard, Kunai's Bane 4th GCD", $"{Balance}, Dawntrail 7.0",
        [
            G("Ten", 6.5), G("Chi", 6), G("Jin", 5.5), G("Suiton", 0.9),
            O("Kassatsu"), G("Spinning Edge"), Pot(Dex), G("Gust Slash"), O("Dokumori"), O("Bunshin"),
            G("Phantom Kamaitachi"), G("Armor Crush"), O("Kunai's Bane"), G("Hyosho Ranryu"), O("Dream Within a Dream"),
            G("Raiton"), O("Ten Chi Jin"), G("Fuma Shuriken"), G("Raiton"), G("Suiton"), O("Meisui"), G("Fleeting Raiju"),
            O("Zesho Meppo"), O("Tenri Jindo"), G("Fleeting Raiju"), O("Bhavacakra"), G("Raiton"), G("Fleeting Raiju"),
        ],
        [
            "Start the Suiton mudras at -6 s and cast it at about -0.9 s: two Raitons fit under Kunai's Bane.",
            "Ideally drift Dokumori one GCD after the opener to align with the other raid buffs.",
            "The mudras of a Ninjutsu (Ten, Chi, Jin) are not checked one by one, only the Ninjutsu.",
        ],
        ["Ten", "Chi", "Jin"]),

        new("nin-3rd", "NIN", "Standard, Kunai's Bane 3rd GCD", $"{Balance}, Dawntrail 7.0",
        [
            G("Ten", 6.5), G("Chi", 6), G("Jin", 5.5), G("Suiton", 0.9),
            O("Kassatsu"), G("Spinning Edge"), Pot(Dex), G("Gust Slash"), O("Dokumori"), O("Bunshin"),
            G("Phantom Kamaitachi"), O("Kunai's Bane"), G("Hyosho Ranryu"), O("Dream Within a Dream"), G("Raiton"),
            O("Ten Chi Jin"), G("Fuma Shuriken"), G("Raiton"), G("Suiton"), O("Meisui"), G("Fleeting Raiju"),
            O("Zesho Meppo"), O("Tenri Jindo"), G("Fleeting Raiju"), G("Armor Crush"), O("Bhavacakra"), G("Raiton"),
            G("Fleeting Raiju"),
        ],
        [
            "Kunai's Bane one GCD earlier: slightly weaker, better at 2.10 GCD.",
            "Same Suiton timing (-6 s, cast at about -0.9 s); drift Dokumori one GCD after the opener.",
            "The mudras of a Ninjutsu (Ten, Chi, Jin) are not checked one by one, only the Ninjutsu.",
        ],
        ["Ten", "Chi", "Jin"]),

        new("sam-standard", "SAM", "Standard opener", $"{Balance}, Dawntrail 7.05",
        [
            O("Meikyo Shisui", 14), O("True North", 5),
            G("Gekko"), Pot(Str), G("Kasha"), O("Ikishoten"), G("Yukikaze"), G("Tendo Setsugekka"), O("Hissatsu: Senei"),
            G("Tendo Kaeshi Setsugekka"), O("Meikyo Shisui"), G("Gekko"), O("Zanshin"), G("Higanbana"), G("Ogi Namikiri"),
            O("Shoha"), G("Kaeshi: Namikiri"), G("Kasha"), O("Hissatsu: Shinten"), G("Gekko"), O("Hissatsu: Gyoten"),
            G("Gyofu"), G("Yukikaze"), O("Hissatsu: Shinten"), G("Tendo Setsugekka"), G("Tendo Kaeshi Setsugekka"),
        ],
        ["Used in most encounters. Depending on phasing it can be better to start with Kasha into Gekko."]),

        new("rpr-2nd-gcd-ac", "RPR", "2nd GCD Arcane Circle", $"{Balance}, Dawntrail 7.0",
        [
            G("Harpe", 1.7),
            G("Shadow of Death"), Pot(Str), G("Soul Slice"), O("Arcane Circle"), O("Gluttony"), G("Executioner's Gibbet"),
            G("Executioner's Gallows"), G("Soul Slice"), G("Plentiful Harvest"), O("Enshroud"), G("Void Reaping"),
            O("Sacrificium"), G("Cross Reaping"), O("Lemure's Slice"), G("Void Reaping"), G("Cross Reaping"),
            O("Lemure's Slice"), G("Communio"), G("Perfectio"), O("Unveiled Gibbet"), G("Gibbet"), G("Shadow of Death"),
            G("Slice"),
        ],
        [
            "Use Soulsow before the pull, then cast Harpe when 2 shows on the countdown (about -1.7 s).",
            "Late weave the tincture and Arcane Circle as much as you can without clipping (about 0.5 s delay).",
            "Plentiful Harvest may clip below 2.47 GCD or with high ping.",
        ]),

        new("vpr-standard", "VPR", "Standard opener", $"{Balance}, Dawntrail 7.5",
        [
            O("Slither", 0.5),
            G("Steel Fangs|Reaving Fangs"), O("Serpent's Ire"), G("Swiftskin's Sting"), G("Vicewinder"), Pot(Dex),
            G("Hunter's Coil"), O("Twinfang Bite"), O("Twinblood Bite"), G("Swiftskin's Coil"), O("Twinblood Bite"),
            O("Twinfang Bite"), G("Reawaken"), G("First Generation"), O("First Legacy"), G("Second Generation"),
            O("Second Legacy"), G("Third Generation"), O("Third Legacy"), G("Fourth Generation"), O("Fourth Legacy"),
            G("Ouroboros"), G("Uncoiled Fury"), O("Uncoiled Twinfang"), O("Uncoiled Twinblood"), G("Uncoiled Fury"),
            O("Uncoiled Twinfang"), O("Uncoiled Twinblood"), G("Hindsting Strike|Hindsbane Fang"), O("Death Rattle"),
            G("Vicewinder"), G("Hunter's Coil"), O("Twinfang Bite"), O("Twinblood Bite"), G("Swiftskin's Coil"),
            O("Twinblood Bite"), O("Twinfang Bite"),
        ],
        ["The first GCD can be Steel Fangs or Reaving Fangs."]),
    ];

    // ------------------------------------------------------------------ Physical ranged

    private const string BardNotes = "Any Burst Shot can be a Refulgent Arrow and the other way around, except the one after Barrage.";
    private const string BardProcs = "Pitch Perfect at three stacks, Heartbreak Shot when ready, Refulgent Arrow procs as they come.";
    private const string BardPull = "Use your first action as the countdown hits zero, not before.";
    private const string Shot = "Burst Shot|Refulgent Arrow";

    private static readonly OpenerDefinition[] Ranged =
    [
        new("brd-adjusted", "BRD", "Adjusted standard opener (2.48)", $"{Balance}, Dawntrail 7.0",
        [
            O("Heartbreak Shot"), G("Stormbite"), O("the Wanderer's Minuet"), O("Empyreal Arrow"), G("Caustic Bite"),
            Pot(Dex), O("Battle Voice"), G(Shot), O("Radiant Finale"), O("Raging Strikes"), G(Shot), O("Barrage"),
            G("Refulgent Arrow"), O("Sidewinder"), G("Radiant Encore"), G("Resonant Arrow"), O("Empyreal Arrow"),
            G(Shot), G(Shot), G("Iron Jaws"), G(Shot), O("Pitch Perfect"),
        ],
        [BardNotes, BardProcs, BardPull]),

        new("brd-249", "BRD", "2.49 standard opener (comfy)", $"{Balance}, Dawntrail 7.05",
        [
            Pot(Dex, 0.7),
            G("Stormbite"), O("Heartbreak Shot"), O("the Wanderer's Minuet"), G("Caustic Bite"), O("Empyreal Arrow"),
            O("Radiant Finale"), G(Shot), O("Battle Voice"), O("Raging Strikes"), G(Shot), O("Barrage"),
            G("Refulgent Arrow"), O("Sidewinder"), G("Radiant Encore"), G("Resonant Arrow"), G(Shot),
            O("Empyreal Arrow"), G(Shot), G("Iron Jaws"), G(Shot), O("Pitch Perfect"),
        ],
        [BardNotes, BardProcs]),

        new("brd-369", "BRD", "3-6-9 opener (2.49 or 2.50)", $"{Balance}, Dawntrail 7.0",
        [
            G("Stormbite"), O("Heartbreak Shot"), O("the Wanderer's Minuet"), G("Caustic Bite"), Pot(Dex),
            O("Radiant Finale"), G(Shot), O("Battle Voice"), O("Raging Strikes"), G(Shot), O("Empyreal Arrow"),
            G("Radiant Encore"), O("Barrage"), G("Refulgent Arrow"), O("Sidewinder"), G("Resonant Arrow"), G(Shot),
            G(Shot), G("Iron Jaws"), O("Empyreal Arrow"), G(Shot), O("Pitch Perfect"),
        ],
        [BardNotes, BardProcs, BardPull]),

        new("mch-standard", "MCH", "Standard opener (Air Anchor)", $"{Balance}, Dawntrail 7.0",
        [
            O("Reassemble", 5), Pot(Dex, 2),
            G("Air Anchor"), O("Checkmate"), O("Double Check"), G("Drill"), O("Barrel Stabilizer"), G("Chain Saw"),
            G("Excavator"), O("Automaton Queen"), O("Reassemble"), G("Drill"), O("Checkmate"), O("Wildfire"),
            G("Full Metal Field"), O("Double Check"), O("Hypercharge"), G("Blazing Shot"), O("Checkmate"),
            G("Blazing Shot"), O("Double Check"), G("Blazing Shot"), O("Checkmate"), G("Blazing Shot"), O("Double Check"),
            G("Blazing Shot"), O("Checkmate"), G("Drill"), O("Double Check"), O("Checkmate"), G("Heated Split Shot"),
            O("Double Check"), G("Heated Slug Shot"), G("Heated Clean Shot"),
        ],
        [
            "A pre-pull Reassemble gets its cooldown rolling early.",
            "Drill and Air Anchor can be swapped if it gains an extra Drill in the fight.",
            "The oGCD order matters little: the second Reassemble must catch the buffs, Wildfire as early as possible in them.",
            "If you use a tincture in the opener, use it 2 s before the pull.",
        ]),

        new("mch-even", "MCH", "Alternative opener (even minute bursts)", $"{Balance}, Dawntrail 7.0",
        [
            O("Reassemble", 5), Pot(Dex, 1),
            G("Air Anchor"), O("Checkmate"), O("Double Check"), G("Drill"), O("Barrel Stabilizer"), G("Chain Saw"),
            O("Reassemble"), G("Excavator"), O("Automaton Queen"), O("Checkmate"), G("Full Metal Field"),
            O("Hypercharge"), O("Wildfire"), G("Blazing Shot"), O("Double Check"), G("Blazing Shot"), O("Checkmate"),
            G("Blazing Shot"), O("Double Check"), G("Blazing Shot"), O("Checkmate"), G("Blazing Shot"), O("Double Check"),
            G("Drill"), O("Checkmate"), O("Double Check"), G("Drill"), O("Checkmate"), O("Double Check"),
            G("Heated Split Shot"), G("Heated Slug Shot"), G("Heated Clean Shot"),
        ],
        [
            "Same setup as the even minute bursts: the second Drill comes after the Hypercharge window.",
            "Reassemble on Excavator to have it in buffs (or on the second or third Drill).",
        ]),

        new("dnc-standard", "DNC", "Standard opener", $"{Balance}, Dawntrail 7.0",
        [
            O("Closed Position", 16), G("Standard Step", 15), G("Emboite|Entrechat|Jete|Pirouette", 13.5),
            G("Emboite|Entrechat|Jete|Pirouette", 12.5), O("Peloton", 5), Pot(Dex, 1),
            G("Double Standard Finish|Single Standard Finish|Standard Finish"), G("Technical Step"),
            G("Emboite|Entrechat|Jete|Pirouette"), G("Emboite|Entrechat|Jete|Pirouette"),
            G("Emboite|Entrechat|Jete|Pirouette"), G("Emboite|Entrechat|Jete|Pirouette"),
            G("Quadruple Technical Finish|Triple Technical Finish|Double Technical Finish|Single Technical Finish|Technical Finish"),
            O("Devilment"), G("Tillana"), O("Flourish"), G("Dance of the Dawn"), O("Fan Dance IV"), G("Last Dance"),
            O("Fan Dance III"), G("Finishing Move"), G("Saber Dance|Starfall Dance"),
        ],
        [
            "Closed Position needs a dance partner: alone on a dummy, turn off the pre-pull.",
            "After the opener, use your priority GCDs.",
        ]),
    ];

    // ------------------------------------------------------------------ Casters

    private static readonly OpenerDefinition[] Casters =
    [
        new("blm-5-7", "BLM", "Standard 5+7 opener", $"{Balance}, Dawntrail 7.2",
        [
            G("Fire III", 4),
            G("High Thunder"), O("Swiftcast"), O("Amplifier"), G("Fire IV"), Pot(Int), O("Ley Lines"), G("Fire IV"),
            G("Fire IV"), G("Fire IV"), G("Fire IV"), G("Xenoglossy"), O("Manafont"), G("Fire IV"), G("Flare Star"),
            G("Fire IV"), G("Fire IV"), G("High Thunder"), G("Fire IV"), G("Fire IV"), G("Fire IV"), G("Fire IV"),
            G("Flare Star"), G("Despair"), O("Transpose"), O("Triplecast"), G("Blizzard III"), G("Blizzard IV"),
            G("Paradox"), O("Transpose"), G("Paradox"), G("Fire III"),
        ],
        [
            "Start casting Fire III about 4 s before the pull, so it lands as the boss is pulled.",
            "Refresh High Thunder just before raid buffs fall off.",
            "The second Ley Lines charge is free to use after the opener; Triplecast and Swiftcast can move for mechanics.",
            "No Firestarter proc from this opener: keep using Paradox first in Astral Fire until the next Manafont.",
        ]),

        new("blm-flare", "BLM", "Alternative single target Flare opener", $"{Balance}, Dawntrail 7.2",
        [
            G("Fire III", 4),
            G("High Thunder"), O("Swiftcast"), O("Amplifier"), G("Fire IV"), Pot(Int), O("Ley Lines"), G("Fire IV"),
            G("Xenoglossy"), G("Fire IV"), G("Fire IV"), G("Despair"), O("Manafont"), G("Fire IV"), G("Fire IV"),
            G("Flare Star"), G("Fire IV"), G("High Thunder"), G("Fire IV"), G("Fire IV"), G("Fire IV"), G("Paradox"),
            O("Triplecast"), G("Flare"), G("Flare Star"), O("Transpose"), G("Blizzard III"), G("Blizzard IV"),
            G("Paradox"), O("Transpose"), G("Fire III"),
        ],
        [
            "A minor loss compared to 5+7, but it gives a Firestarter proc for the next Astral Fire.",
            "Start casting Fire III about 4 s before the pull. Xenoglossy can move, ideally within the raid buffs.",
        ]),

        new("smn-general", "SMN", "General opener", $"{Balance}, Dawntrail 7.5",
        [
            G("Ruin III", 1.5),
            G("Summon Solar Bahamut"), Pot(Int), G("Umbral Impulse"), O("Searing Light"), G("Umbral Impulse"),
            G("Umbral Impulse"), O("Energy Drain"), G("Umbral Impulse"), O("Enkindle Solar Bahamut"), O("Necrotize"),
            G("Umbral Impulse"), O("Sunflare"), O("Necrotize"), G("Umbral Impulse"), O("Searing Flash"),
            G("Summon Titan II"), G("Topaz Rite"), O("Mountain Buster"), G("Topaz Rite"), O("Mountain Buster"),
            G("Topaz Rite"), O("Mountain Buster"), G("Topaz Rite"), O("Mountain Buster"), G("Summon Garuda II"),
            O("Swiftcast"), G("Slipstream"),
        ],
        [
            "Searing Light goes after the Bahamut GCD, to avoid a dead GCD in the buff.",
            "You can skip the pre-cast Ruin III and summon Bahamut at 0 s (turn off the pre-pull).",
        ]),

        new("rdm-standard", "RDM", "Standard opener", $"{Balance}, Dawntrail 7.0",
        [
            G("Veraero III", 5),
            G("Verthunder III"), O("Swiftcast"), Pot(Int), G("Verthunder III"), O("Fleche"), O("Acceleration"),
            G("Verthunder III"), O("Embolden"), O("Manafication"), G("Enchanted Riposte"), O("Contre Sixte"),
            G("Enchanted Zwerchhau"), O("Engagement"), G("Enchanted Redoublement"), O("Corps-a-corps"), G("Verholy"),
            O("Vice of Thorns"), G("Scorch"), O("Engagement"), O("Corps-a-corps"), G("Resolution"), O("Prefulgence"),
            G("Grand Impact"), O("Acceleration"), G("Verfire"), G("Grand Impact"), G("Verthunder III"), O("Fleche"),
            G("Veraero III"), G("Verfire"), G("Verthunder III"), G("Verstone"), G("Veraero III"), O("Swiftcast"),
            G("Veraero III"), O("Contre Sixte"),
        ],
        [
            "Highest damage, strictest rotation: up to 2 minutes, the oGCD alignment is tight (Acceleration for a Fleche + Contre Sixte double weave at about 1:18).",
        ]),

        new("rdm-early-c6", "RDM", "Early Contre Sixte opener", $"{Balance}, Dawntrail 7.0",
        [
            G("Veraero III", 5),
            G("Verthunder III"), O("Acceleration"), Pot(Int), G("Verthunder III"), O("Fleche"), O("Contre Sixte"),
            G("Grand Impact"), O("Embolden"), O("Manafication"), G("Enchanted Riposte"), O("Engagement"),
            G("Enchanted Zwerchhau"), O("Corps-a-corps"), G("Enchanted Redoublement"), O("Vice of Thorns"), G("Verholy"),
            O("Engagement"), O("Corps-a-corps"), G("Scorch"), G("Resolution"), O("Acceleration"), O("Prefulgence"),
            G("Grand Impact"), G("Verfire"), G("Verthunder III"), G("Veraero III"), O("Fleche"), G("Verstone"),
            G("Veraero III"), G("Verfire"), G("Veraero III"), O("Swiftcast"), O("Contre Sixte"), G("Verthunder III"),
        ],
        [
            "A bit less damage in buffs, more freedom to move, and an earlier Contre Sixte.",
            "Don't use Swiftcast before the second Contre Sixte.",
        ]),

        new("rdm-single-weave", "RDM", "Single weave tincture opener", $"{Balance}, Dawntrail 7.0",
        [
            G("Veraero III", 5),
            G("Verthunder III"), O("Fleche"), O("Acceleration"), G("Verthunder III"), Pot(Int), G("Grand Impact"),
            O("Embolden"), O("Manafication"), G("Enchanted Riposte"), O("Contre Sixte"), G("Enchanted Zwerchhau"),
            O("Engagement"), G("Enchanted Redoublement"), O("Corps-a-corps"), G("Verholy"), O("Vice of Thorns"),
            O("Engagement"), G("Scorch"), O("Corps-a-corps"), O("Swiftcast"), G("Resolution"), O("Acceleration"),
            O("Prefulgence"), G("Grand Impact"), G("Veraero III"), G("Verthunder III"), O("Fleche"), G("Verstone"),
            G("Veraero III"), G("Verfire"), G("Verthunder III"), G("Jolt III"), G("Veraero III"), G("Jolt III"),
            G("Verthunder III"), O("Contre Sixte"),
        ],
        ["Slightly lower potency, for when your ping makes double weaving the tincture hard. The last Contre Sixte drifts by one cast."]),

        new("pct-2nd-gcd", "PCT", "2nd GCD Starry opener", $"{Balance}, Dawntrail 7.2",
        [
            G("Rainbow Drip", 4.5),
            Pot(Int), O("Pom Muse"), O("Striking Muse"), G("Wing Motif"), O("Starry Muse"), G("Hammer Stamp"),
            O("Subtractive Palette"), G("Blizzard in Cyan"), G("Stone in Yellow"), G("Thunder in Magenta"),
            G("Comet in Black"), O("Winged Muse"), O("Mog of the Ages"), G("Star Prism"), G("Hammer Brush"),
            G("Polishing Hammer"), G("Rainbow Drip"), G("Fire in Red"), O("Swiftcast"), G("Aero in Green"),
        ],
        [
            "Paint your motifs before the pull.",
            "The spells of the window can be in any order, as long as all of them fit in the nine spell window.",
            "May clip slightly with ping because of the triple weave after Rainbow Drip.",
        ]),

        new("pct-3rd-gcd", "PCT", "3rd GCD Starry opener", $"{Balance}, Dawntrail 7.2",
        [
            G("Rainbow Drip", 4.5),
            O("Striking Muse"), G("Holy in White"), Pot(Int), O("Pom Muse"), G("Wing Motif"), O("Starry Muse"),
            G("Hammer Stamp"), O("Subtractive Palette"), G("Blizzard in Cyan"), G("Stone in Yellow"),
            G("Thunder in Magenta"), G("Comet in Black"), O("Winged Muse"), O("Mog of the Ages"), G("Star Prism"),
            G("Hammer Brush"), G("Polishing Hammer"), G("Rainbow Drip"), G("Fire in Red"), O("Swiftcast"),
            G("Claw Motif"), O("Clawed Muse"),
        ],
        [
            "Paint your motifs before the pull.",
            "For 3rd GCD buff timelines, for high ping, or when Holy in White is the better choice.",
        ]),
    ];

    public static IReadOnlyList<OpenerDefinition> All { get; } = [.. Tanks, .. Healers, .. Melee, .. Ranged, .. Casters];

    public static IEnumerable<OpenerDefinition> ForJob(string job) => All.Where(o => o.Job == job);
}
