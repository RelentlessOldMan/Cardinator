using System.Runtime.CompilerServices;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Runs once before any test in this assembly.
/// <para>
/// Keeps the suite OFFLINE. Several tests construct a real <c>MainWindow</c>/<c>FrameDesignWindow</c>, and
/// those constructors kick off <see cref="SymbolService.PrimeAsync"/> — which downloads the whole Scryfall
/// symbology plus ~80 SVGs. That made every local and CI run do hidden network I/O, left a fire-and-forget
/// task outliving the test's STA thread, and made symbol rendering depend on run history (real SVGs if a
/// previous run had cached them, drawn fallback pips otherwise).
/// </para>
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init() => SymbolService.SuppressPrime = true;
}
