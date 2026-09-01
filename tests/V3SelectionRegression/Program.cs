using System.Reflection;
using System.Text.Json;
using CskinNative.Services;

var failures = new List<string>();

Run("cell 0 keeps the local Hecarim pick", () =>
{
    const string payload = """
        {
          "localPlayerCellId": 0,
          "myTeam": [
            { "cellId": 0, "summonerId": 12345, "championId": 120 }
          ],
          "actions": [[
            { "type": "pick", "actorCellId": 0, "championId": 120 },
            { "type": "pick", "actorCellId": 1, "championId": 35 }
          ]]
        }
        """;

    var selection = Parse("ParseSelection", payload, 12345);
    Equal(120, selection.ResolvedChampionId, "another player's Shaco action replaced local Hecarim");
});

Run("Lobby gameflow data is not treated as a live selection", () =>
{
    const string payload = """
        {
          "phase": "Lobby",
          "gameData": {
            "playerChampionSelections": [
              { "summonerId": 12345, "championId": 35, "selectedSkinId": 35000 }
            ]
          }
        }
        """;

    var selection = Parse("ParseGameflowSelection", payload, 12345);
    Equal(0, selection.ResolvedChampionId, "Lobby retained a stale champion selection");
});

Run("gameflow requires an explicit local summoner match", () =>
{
    const string payload = """
        {
          "phase": "ChampSelect",
          "gameData": {
            "playerChampionSelections": [
              { "summonerId": 99999, "championId": 35, "selectedSkinId": 35000 }
            ]
          }
        }
        """;

    var selection = Parse("ParseGameflowSelection", payload, 0);
    Equal(0, selection.ResolvedChampionId, "a sole non-local candidate was accepted without identity");
});

Run("live gameflow accepts an explicitly matched local summoner", () =>
{
    const string payload = """
        {
          "phase": "ChampSelect",
          "gameData": {
            "playerChampionSelections": [
              { "summonerId": 12345, "championId": 120, "selectedSkinId": 120000 }
            ]
          }
        }
        """;

    var selection = Parse("ParseGameflowSelection", payload, 12345);
    Equal(120, selection.ResolvedChampionId, "the explicitly matched local Hecarim was rejected");
});

Run("a single champion sample cannot change the UI", () =>
{
    var tracker = new ClientSelectionStabilizer(requiredSamples: 2);
    var observation = tracker.Observe(120, 120000);

    False(observation.ShouldSynchronize, "one Hecarim sample was accepted immediately");
    Equal(1, observation.ConsecutiveSamples, "first sample count");
});

Run("a stable champion synchronizes once and does not fight manual browsing", () =>
{
    var tracker = new ClientSelectionStabilizer(requiredSamples: 2);

    False(tracker.Observe(120, 120000).ShouldSynchronize, "first sample");
    True(tracker.Observe(120, 120000).ShouldSynchronize, "second stable sample");
    False(tracker.Observe(120, 120000).ShouldSynchronize, "unchanged LCU value repeated synchronization");
    False(tracker.Observe(120, 120000).ShouldSynchronize, "unchanged LCU value kept overriding manual selection");
});

Run("a transient wrong champion is ignored before a real client change", () =>
{
    var tracker = new ClientSelectionStabilizer(requiredSamples: 2);
    tracker.Observe(120, 120000);
    tracker.Observe(120, 120000);

    False(tracker.Observe(35, 35000).ShouldSynchronize, "one Shaco sample replaced Hecarim");
    False(tracker.Observe(64, 64000).ShouldSynchronize, "first new champion sample");
    True(tracker.Observe(64, 64000).ShouldSynchronize, "stable new champion did not synchronize");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAILED ({failures.Count})");
    foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine("PASS: all V3 selection regression tests");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
    }
}

static ChampionSelection Parse(string methodName, string json, long summonerId)
{
    var method = typeof(LeagueClient).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Missing parser {methodName}");
    using var document = JsonDocument.Parse(json);
    return (ChampionSelection)(method.Invoke(null, [document.RootElement, summonerId])
        ?? throw new InvalidOperationException($"Parser {methodName} returned null"));
}

static void Equal(int expected, int actual, string message)
{
    if (expected != actual) throw new InvalidOperationException($"{message}; expected={expected}, actual={actual}");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void False(bool value, string message) => True(!value, message);
