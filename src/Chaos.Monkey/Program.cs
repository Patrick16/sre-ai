using k8s;
using k8s.Models;

// A tiny Chaos Monkey (Phase 9, docs/18): what PodChaos "pod-kill" does, written out by hand to see that there is
// no magic - list the pods, pick one at random, DELETE it through the Kubernetes API, let the controller heal.
//
//   dotnet run --project src/Chaos.Monkey -- --app orders-api                       # dry run: only says what it would kill
//   dotnet run --project src/Chaos.Monkey -- --app orders-api --kill --every 60 --for 10
//
// The safety rules are the point - they are the same ideas as the agent's guardrails:
//   - only namespace "sandbox" (hard-coded allowlist, not a flag);
//   - only pods of ONE app, chosen by its label;
//   - never the last ready pods: at least --keep stay ready (default 2), otherwise this round is skipped;
//   - a time limit (--for minutes) and Ctrl+C both stop it; dry run unless --kill is given.

string[] allowedNamespaces = ["sandbox"];

var app = Arg("--app") ?? Fail("--app <name> is required, e.g. --app orders-api");
var ns = Arg("--namespace") ?? "sandbox";
// A typo like "--every abc" is a clear message and exit code 2, not an unhandled FormatException.
var everySeconds = int.TryParse(Arg("--every") ?? "60", out var every) && every > 0 ? every : FailInt("--every must be a positive whole number of seconds");
var forMinutes = double.TryParse(Arg("--for") ?? "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) && minutes >= 0
    ? minutes : FailDouble("--for must be a number of minutes (0 = one round)");
var keep = int.TryParse(Arg("--keep") ?? "2", out var keepPods) && keepPods >= 0 ? keepPods : FailInt("--keep must be a whole number >= 0");
var kill = args.Contains("--kill");

if (!allowedNamespaces.Contains(ns)) Fail($"namespace '{ns}' is not allowed (only: {string.Join(", ", allowedNamespaces)})");
if (app is "postgres") Fail("refusing to target the database: it has one replica - killing it is an outage, not an experiment");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
if (forMinutes > 0) cts.CancelAfter(TimeSpan.FromMinutes(forMinutes));

var kube = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile()); // your kubeconfig (kind-sre-ai)
Log($"chaos monkey: app={app} ns={ns} every={everySeconds}s for={(forMinutes > 0 ? $"{forMinutes} min" : "one round")} keep>={keep} mode={(kill ? "KILL" : "dry run")}");

do
{
    var pods = (await kube.CoreV1.ListNamespacedPodAsync(ns, labelSelector: $"app.kubernetes.io/name={app}", cancellationToken: CancellationToken.None)).Items;
    var ready = pods.Where(p => p.Metadata.DeletionTimestamp is null && IsReady(p)).ToList();
    if (ready.Count <= keep)
    {
        Log($"skip: only {ready.Count} ready pod(s) of {app}, must keep {keep} - the system hasn't healed from the last kill yet");
    }
    else
    {
        var victim = ready[Random.Shared.Next(ready.Count)];
        if (kill)
        {
            // gracePeriodSeconds 0: like a crash, not a polite shutdown (no preStop, no draining).
            try
            {
                await kube.CoreV1.DeleteNamespacedPodAsync(victim.Name(), ns, gracePeriodSeconds: 0);
                Log($"killed {victim.Name()} on {victim.Spec.NodeName} ({ready.Count - 1} of {pods.Count} left ready)");
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Log($"skip: {victim.Name()} was already gone (something else replaced it first)");
            }
        }
        else
        {
            Log($"dry run: would kill {victim.Name()} on {victim.Spec.NodeName} ({ready.Count} ready); add --kill to do it");
        }
    }

    if (forMinutes <= 0) break;
    try { await Task.Delay(TimeSpan.FromSeconds(everySeconds), cts.Token); }
    catch (OperationCanceledException) { break; }
} while (!cts.IsCancellationRequested);

Log("chaos monkey: done");
return 0;

static bool IsReady(V1Pod p) => p.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;

static void Log(string message) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss}Z {message}");

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int FailInt(string message) { Fail(message); return 0; }

static double FailDouble(string message) { Fail(message); return 0; }

static string Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(2);
    return "";
}
