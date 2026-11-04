// =========================================================================================
// Tool Name:    icPA - Bubble Sweep
// Class:        icPABubbleSweep
// Category:     Indicator
// Version:      16.2.1
// Platform:     NinjaTrader 8.0+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/bubble-sweep
// YouTube:      https://youtube.com/@icPriceAction
// Support:      https://buymeacoffee.com/icpriceaction
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  Shows large aggressive trades (sweeps) as volume-sized bubbles on their candle,
//               with same-side clustering zones and absorption rings. No Tick Replay required.
//
// Changelog:
//   v16.2.1  - Initial public release. Cleaned and optimized by icPriceAction.
//            - Renamed to icPABubbleSweep (icPA folder); Name and Description updated.
//            - Default colors changed to complement the icPriceAction chart palette.
//            - Parameters ordered sequentially within feature groups (General, Visuals,
//              Clustering Zones, Absorption, Diagnostics); display names de-numbered.
//            - Text formats created outside OnRender; render loop no longer allocates per bubble.
//            - Safety caps on stored bubbles and ended zones; no per-call lambdas in hot paths.
//            - One-time warning when Calculate is not On Each Tick in real time.
//   v16.2.0              - Code audit: Adaptive Percentile default 99.95; absorption windows
//                          use NinjaTrader's clock (Playback-safe); mouse handler and brush fixes.
//   v16.1.0              - Zone window in minutes; zones expire at session end; Merge Bubbles
//                          Within Bar.
//   v16.0.0              - Release prep: diagnostics off by default (bid/ask warning only);
//                          max diameter clamp; softer size gradient; setting descriptions.
//   v15.1.0              - Tooltip Font Size setting; stronger size gradient.
//   v15.0.0              - Hover tooltips; Intensity By Size; absorption ring outside outline.
//   v14.2.0              - Removed Spread Bubbles Across Bar; Show Volume Text off and Use
//                          Outline Only on by default.
//   v14.1.0              - Absorption Max Continuation default changed to 0 ticks.
//   v14.0.0              - Absorption detection; BuyAbsorption / SellAbsorption series.
//   v13.0.0              - Clustering zones; BuyZoneSignal / SellZoneSignal series.
//   v12.0.0              - Adaptive percentile threshold; SweepThreshold series; constructor
//                          now (minSweepVolume, useAdaptiveThreshold, adaptivePercentile).
//   v11.1.0              - Fixed bubbles landing between candles when spread across the bar.
//   v11.0.0              - SharpDX bubble rendering; bubble area scales with volume; pixel sizes.
//   v10.2.0              - Classification from bid/ask stamped on each trade; Bid/Ask series removed.
//   v10.1.0              - Diagnostics output.
//   v10.0.0              - Bid/ask aggressor classification with tick-rule fallback.
//   v9.1.0               - Bubbles anchored to the bar the sweep was attributed to.
//   v9.0.0               - Output series; real-time signal, bar attribution and flush fixes.
//   v8.0.0               - XmlIgnore on signal series; cross-series safety checks.
// =========================================================================================

// -----------------------------------------------------------------------------------------
//  OVERVIEW
//
//  WHAT IT SHOWS
//    - Bubbles: trades at the same timestamp and on the same side are combined into a
//      sweep. Sweeps above the threshold are drawn as bubbles on their candle, at their
//      volume-weighted price. Bubble area grows with volume; larger sweeps are bolder.
//    - Threshold: adaptive by default (top percentile of recent sweep sizes), so it
//      works on any instrument and session. A fixed volume can be used instead.
//    - Buy/sell side: taken from the bid/ask stamped on each trade in the tick data
//      (at/above ask = buy, at/below bid = sell), with the tick rule as a fallback.
//    - Clustering zones: several same-side bubbles close together in price and time
//      form a zone that extends right until a bar closes through it or the session ends.
//    - Absorption: a bubble whose sweep could not push price even one tick further
//      within a few seconds gets a contrasting ring.
//    - Tooltips: hover a bubble for its volume, price levels, time and threshold.
//
//  DATA REQUIREMENT
//    Historical tick data. Accurate buy/sell sides need tick data stamped with bid/ask
//    (e.g. NinjaTrader's own data feed). Without it, the tick rule is used and a warning
//    is printed to the NinjaScript Output window. Calculate: On Each Tick.
//
//  STRATEGY USE
//    A hosting strategy must add the same extra series in State.Configure:
//      AddDataSeries(BarsPeriodType.Tick, 1);
//    Constructor: icPABubbleSweep(minSweepVolume, useAdaptiveThreshold, adaptivePercentile)
//
//    Output series (one value per primary bar):
//      BuySweepCount, SellSweepCount        number of qualifying sweeps
//      BuySweepVolume, SellSweepVolume      total volume of those sweeps
//      LargestBuySweep, LargestSellSweep    largest single sweep
//      BullishSweepSignal, BearishSweepSignal  1 if any buy / sell sweep, else 0
//      SweepThreshold                       threshold in effect (0 while warming up)
//      BuyZoneSignal, SellZoneSignal        1 on the bar a zone forms
//      BuyAbsorption, SellAbsorption        1 on the bar absorption is CONFIRMED
//                                           (end of the window, so no look-ahead)
//
//    Historical note: trades that share a bar's closing timestamp arrive after that bar
//    has closed. They are attributed to the correct bar, but in historical processing a
//    bar's values are final only once the next bar has been processed. In real time,
//    values update on every tick.
// -----------------------------------------------------------------------------------------

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.icPA
{
    public class icPABubbleSweep : Indicator
    {
        public const string IndicatorVersion = "16.2.1";

        // Adaptive threshold: sweeps needed before the percentile is trusted,
        // and how often (in sweeps) it is recalculated.
        private const int AdaptiveWarmupSweeps   = 500;
        private const int AdaptiveRecomputeEvery = 250;

        // Memory safety caps. Only reached with very low thresholds over long histories;
        // the oldest bubbles / ended zones are dropped first.
        private const int MaxStoredBubbles = 250000;
        private const int BubbleTrimBatch  = 25000;
        private const int MaxStoredZones   = 5000;
        private const int ZoneTrimBatch    = 500;

        // Volume text font sizes (px)
        private const int MinVolumeFont = 9;
        private const int MaxVolumeFont = 18;

        // Series indices
        private const int BipPrimary = 0;
        private const int BipLast    = 1;

        // ---------------------------------------------------------------------
        // Data types
        // ---------------------------------------------------------------------
        private class BarSweepStats
        {
            public int    BuyCount;
            public int    SellCount;
            public double BuyVolume;
            public double SellVolume;
            public double LargestBuy;
            public double LargestSell;
            public bool   BuyZoneFormed;
            public bool   SellZoneFormed;
            public bool   BuyAbsorbed;
            public bool   SellAbsorbed;
        }

        // A bubble being watched for absorption
        private class AbsorptionWatch
        {
            public SweepRecord Record;
            public int         Direction;
            public double      ReferencePrice; // sweep's extreme price in its direction
            public DateTime    Expiry;         // time at which the window ends (chart time zone)
            public int         TradesSeen;
        }

        // A bubble remembered for cluster detection
        private class ClusterPoint
        {
            public int      Bar;
            public DateTime Time;
            public double   Price;
            public double   Volume;
        }

        // A clustering zone
        private class Zone
        {
            public int    Direction;   // 1 = buy zone (support), -1 = sell zone (resistance)
            public int    StartBar;    // first bubble of the cluster
            public int    CreatedBar;  // bar of the bubble that completed the cluster
            public int    EndBar = -1; // bar that closed through it or ended its session; -1 = active
            public double Low;
            public double High;
            public int    BubbleCount;
            public double TotalVolume;
        }

        // A completed sweep, kept for rendering
        private class SweepRecord
        {
            public int      Bar;
            public double   Price;      // volume-weighted average price of the sweep
            public double   Volume;
            public double   Threshold;  // threshold in effect when the sweep happened
            public int      Direction;  // 1 = buy, -1 = sell
            public DateTime Time;
            public double   High;       // highest and lowest trade price in the sweep
            public double   Low;
            public bool     Absorbed;          // set later, once absorption is confirmed
            public int      MergedCount = 1;   // sweeps combined into this bubble
        }

        // Rolling distribution of sweep sizes, stored as a log-scale histogram
        // (about 5% resolution), so percentiles are cheap to query.
        private class AdaptiveWindow
        {
            private const int BucketCount = 1000;
            private const int Offset      = 430; // covers volumes from ~1e-9 to ~1e12
            private static readonly double LnBase = Math.Log(1.05);

            private readonly double[] buffer;
            private readonly int[]    counts = new int[BucketCount];
            private int head;
            private int filled;

            public AdaptiveWindow(int size) { buffer = new double[size]; }

            public int Count { get { return filled; } }

            public void Add(double volume)
            {
                if (filled == buffer.Length)
                    counts[Bucket(buffer[head])]--;
                else
                    filled++;

                buffer[head] = volume;
                counts[Bucket(volume)]++;
                head = (head + 1) % buffer.Length;
            }

            // Approximate volume at percentile p (0-100).
            public double Percentile(double p)
            {
                if (filled == 0) return double.NaN;
                long rank = (long)Math.Ceiling(p / 100.0 * filled);
                if (rank < 1) rank = 1;
                long cumulative = 0;
                for (int b = 0; b < BucketCount; b++)
                {
                    cumulative += counts[b];
                    if (cumulative >= rank) return LowerEdge(b);
                }
                return LowerEdge(BucketCount - 1);
            }

            // Share of the window (0-1) at or above a volume.
            public double ShareAtOrAbove(double volume)
            {
                if (filled == 0) return 0;
                long c = 0;
                for (int b = Bucket(volume); b < BucketCount; b++) c += counts[b];
                return (double)c / filled;
            }

            private static int Bucket(double volume)
            {
                if (volume <= 0) return 0;
                int b = (int)Math.Floor(Math.Log(volume) / LnBase) + Offset;
                return b < 0 ? 0 : (b >= BucketCount ? BucketCount - 1 : b);
            }

            private static double LowerEdge(int bucket)
            {
                return Math.Exp((bucket - Offset) * LnBase);
            }
        }

        private AdaptiveWindow adaptiveWindow;
        private double         cachedAdaptiveThreshold = double.NaN;
        private int            sweepsSinceRecompute;
        private long           sweepsTotal;

        // Rendered sweeps, sorted by bar. Written on the data thread, read on the UI thread.
        private List<SweepRecord> sweeps;
        private readonly object   sweepLock = new object(); // guards sweeps and zones

        // Clustering zones
        private List<Zone>          zones;          // all zones, for rendering
        private List<Zone>          activeZones;    // unbroken zones
        private List<ClusterPoint>  recentBuyPoints;
        private List<ClusterPoint>  recentSellPoints;
        private int                 lastZoneCheckBar = -1;
        private long                zonesCreated;

        // Absorption
        private List<AbsorptionWatch> absorptionWatches;
        private long                  absorbedCount;
        private long                  notAbsorbedCount;

        // Sweep aggregation state
        private bool        hasOpenSweep;
        private DateTime    currentSweepTime;
        private int         currentSweepDirection;
        private int         currentSweepBar;
        private double      currentSweepVolume;
        private double      currentSweepVolumeXPrice;
        private double      currentSweepHigh;
        private double      currentSweepLow;

        // Tick-rule classification state (fallback)
        private bool        hasLastTick;
        private double      lastTickPrice;
        private int         previousTickDirection = 1;

        // Classification counters
        private long        tradesByQuote;
        private long        tradesByTickRule;

        // Diagnostics
        private long        fallbackNoQuote;
        private long        fallbackLockedOrCrossed;
        private long        fallbackInsideSpread;
        private long        sweepsRecorded;
        private System.Diagnostics.Stopwatch processingTimer;

        // Bar attribution state
        private bool                            primaryIsTickBased;
        private int                             primaryTicksPerBar;
        private Dictionary<int, int>            ticksAttributed;
        private Dictionary<int, BarSweepStats>  statsByBar;
        private readonly List<int>              pruneKeys = new List<int>(); // reused, avoids per-bar allocation

        // Real-time flush state
        private System.Timers.Timer flushTimer;
        private Action<object>      flushAction; // cached so the timer does not allocate a delegate every 100 ms
        private DateTime            lastTickArrivalUtc;
        private volatile bool       isTerminating;

        // Rendering resources (UI thread). Brushes are rebuilt whenever the render target changes.
        private SharpDX.Direct2D1.Brush dxBuyFill;
        private SharpDX.Direct2D1.Brush dxSellFill;
        private SharpDX.Direct2D1.Brush dxBuyStroke;
        private SharpDX.Direct2D1.Brush dxSellStroke;
        private SharpDX.Direct2D1.Brush dxText;
        private SharpDX.Direct2D1.Brush dxBuyZone;
        private SharpDX.Direct2D1.Brush dxSellZone;
        private SharpDX.Direct2D1.Brush dxAbsorption;
        private SharpDX.Direct2D1.Brush dxTooltipBackground;
        private SharpDX.Direct2D1.Brush dxTooltipText;

        // Text formats are device-independent: created once in OnRenderTargetChanged (never in
        // OnRender) and disposed in State.Terminated. Indexed by font size.
        private SharpDX.DirectWrite.TextFormat[] volumeTextFormats;
        private SharpDX.DirectWrite.TextFormat   tooltipFormat;

        // Tooltip state (UI thread only). A struct, so the render loop does not allocate.
        private struct RenderedBubble
        {
            public float       X;
            public float       Y;
            public float       Radius;
            public SweepRecord Record;
        }
        private readonly List<RenderedBubble> renderedBubbles = new List<RenderedBubble>();
        private SweepRecord  hoveredRecord;
        private ChartControl mouseChart; // chart whose mouse events are subscribed, if any

        // Output series (backing fields)
        private Series<double> buySweepCount;
        private Series<double> sellSweepCount;
        private Series<double> buySweepVolume;
        private Series<double> sellSweepVolume;
        private Series<double> largestBuySweep;
        private Series<double> largestSellSweep;
        private Series<double> bullishSweepSignal;
        private Series<double> bearishSweepSignal;
        private Series<double> sweepThreshold;
        private Series<double> buyZoneSignal;
        private Series<double> sellZoneSignal;
        private Series<double> buyAbsorption;
        private Series<double> sellAbsorption;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description         = "Shows large aggressive trades (sweeps) as bubbles sized by volume, with clustering zones and absorption rings. "
                                    + "Buy/sell side comes from the bid/ask stamped on each trade. Requires historical tick data (bid/ask-stamped for accurate sides).\r\n"
                                    + "Developed by Istvan Cebrian | https://icpriceaction.com\n"
                                    + "Support this free tool: https://buymeacoffee.com/icpriceaction";
                Name                = "icPA - Bubble Sweep";
                Calculate           = Calculate.OnEachTick;
                IsOverlay           = true;
                DisplayInDataBox    = false;
                PaintPriceMarkers   = false;

                // 1. General
                UseAdaptiveThreshold    = true;
                AdaptivePercentile      = 99.95;
                AdaptiveLookback        = 20000;
                AdaptiveMinimumVolume   = 0;
                MinSweepVolume          = 100;
                RealtimeFlushDelayMs    = 300;

                // 2. Visuals (colors chosen for the icPriceAction #E6E7E8 chart background)
                UseOutlineOnly          = true;
                MinCircleSize           = 20;
                MaxCircleSize           = 90;
                CircleOpacity           = 40;
                IntensityBySize         = true;
                BuyBubbleColor          = Brushes.SeaGreen;
                SellBubbleColor         = Brushes.Firebrick;
                ShowVolumeText          = false;
                TextColor               = Brushes.Black;
                MergeBubbles            = true;
                MergeDistanceTicks      = 2;
                ShowTooltips            = true;
                TooltipFontSize         = 15;

                // 3. Clustering Zones
                ShowZones               = true;
                ZoneMinBubbles          = 3;
                ZonePriceRangeTicks     = 6;
                ZoneWindowMinutes       = 5;
                ZonesExpireAtSessionEnd = true;
                ShowBrokenZones         = true;
                ZoneOpacity             = 20;

                // 4. Absorption
                ShowAbsorption          = true;
                AbsorptionWindowSeconds = 5;
                AbsorptionMaxTicks      = 0;
                AbsorptionRingColor     = Brushes.DarkGoldenrod;

                // 5. Diagnostics
                PrintDiagnostics        = false;
            }
            else if (State == State.Configure)
            {
                // Keep size settings consistent: the maximum can never be below the base.
                if (MaxCircleSize < MinCircleSize)
                    MaxCircleSize = MinCircleSize;

                AddDataSeries(BarsPeriodType.Tick, 1); // BipLast
            }
            else if (State == State.DataLoaded)
            {
                buySweepCount       = new Series<double>(this);
                sellSweepCount      = new Series<double>(this);
                buySweepVolume      = new Series<double>(this);
                sellSweepVolume     = new Series<double>(this);
                largestBuySweep     = new Series<double>(this);
                largestSellSweep    = new Series<double>(this);
                bullishSweepSignal  = new Series<double>(this);
                bearishSweepSignal  = new Series<double>(this);
                sweepThreshold      = new Series<double>(this);
                buyZoneSignal       = new Series<double>(this);
                sellZoneSignal      = new Series<double>(this);
                buyAbsorption       = new Series<double>(this);
                sellAbsorption      = new Series<double>(this);

                absorptionWatches = new List<AbsorptionWatch>();
                zones            = new List<Zone>();
                activeZones      = new List<Zone>();
                recentBuyPoints  = new List<ClusterPoint>();
                recentSellPoints = new List<ClusterPoint>();

                adaptiveWindow  = new AdaptiveWindow(AdaptiveLookback);
                sweeps          = new List<SweepRecord>();
                statsByBar      = new Dictionary<int, BarSweepStats>();
                ticksAttributed = new Dictionary<int, int>();

                primaryIsTickBased = BarsArray[0].BarsPeriod.BarsPeriodType == BarsPeriodType.Tick;
                primaryTicksPerBar = BarsArray[0].BarsPeriod.Value;

                // Measures historical processing only (data is already loaded at this point).
                processingTimer = System.Diagnostics.Stopwatch.StartNew();
            }
            else if (State == State.Historical)
            {
                // Hover tooltips need mouse events from the chart (no-op without a chart).
                SubscribeMouse();
            }
            else if (State == State.Transition)
            {
                if (processingTimer != null)
                    processingTimer.Stop();
            }
            else if (State == State.Realtime)
            {
                PrintClassificationSummary();

                // Prerequisite check, printed once.
                if (Calculate != Calculate.OnEachTick)
                    Print(string.Format("icPABubbleSweep [{0}]: Calculate is set to {1}. On Each Tick is recommended so real-time sweeps are attributed to the correct bar and zones and absorption update on time. A hosting strategy passes its own Calculate setting to this indicator.",
                        Instrument.FullName, Calculate));

                lastTickArrivalUtc = DateTime.UtcNow;
                if (flushTimer == null)
                {
                    flushAction = o => FlushStaleSweep();
                    flushTimer = new System.Timers.Timer(100) { AutoReset = true };
                    flushTimer.Elapsed += OnFlushTimerElapsed;
                    flushTimer.Start();
                }
            }
            else if (State == State.Terminated)
            {
                isTerminating = true;
                UnsubscribeMouse();
                if (flushTimer != null)
                {
                    flushTimer.Stop();
                    flushTimer.Elapsed -= OnFlushTimerElapsed;
                    flushTimer.Dispose();
                    flushTimer = null;
                }
                flushAction = null;
                DisposeDxBrushes();
                DisposeTextFormats();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == BipPrimary)
            {
                // Write current bar, and re-write the previous bar to pick up
                // boundary trades that arrived after it closed (historical sequencing).
                WriteStatsForBar(CurrentBars[0]);
                if (CurrentBars[0] > 0)
                    WriteStatsForBar(CurrentBars[0] - 1);

                double threshold = CurrentThreshold();
                sweepThreshold[0] = double.IsInfinity(threshold) ? 0 : threshold;

                // Zone breaks are checked on closed bars only, so history and real time agree.
                // Historical: each call is a closed bar. Real time: the first tick of a new
                // bar means the previous bar has closed.
                bool historical = State == State.Historical;
                int  closedBar  = historical ? CurrentBars[0] : CurrentBars[0] - 1;
                if ((historical || IsFirstTickOfBar) && closedBar >= 0 && closedBar > lastZoneCheckBar)
                {
                    CheckZoneBreaks(closedBar, historical ? Closes[0][0] : Closes[0][1]);
                    lastZoneCheckBar = closedBar;
                }

                // A new trading session ends the previous session's zones and clusters.
                if (IsFirstTickOfBar && BarsArray[0].IsFirstBarOfSession && CurrentBars[0] > 0)
                    EndSessionZones(CurrentBars[0]);

                if (IsFirstTickOfBar)
                    PruneOldState();
            }
            else if (BarsInProgress == BipLast)
            {
                if (CurrentBars[0] < 0 || CurrentBars[BipLast] < 0) return; // a series has not started yet

                // Bid/ask stamped on this trade by the data provider.
                int    idx = CurrentBars[BipLast];
                double bid = BarsArray[BipLast].GetBid(idx);
                double ask = BarsArray[BipLast].GetAsk(idx);

                // In real time, fall back to the live quote if the trade is not stamped.
                if (State == State.Realtime && (bid <= 0 || ask <= 0))
                {
                    bid = GetCurrentBid(BipLast);
                    ask = GetCurrentAsk(BipLast);
                }

                ProcessTrade(Closes[BipLast][0], Volumes[BipLast][0], Times[BipLast][0], bid, ask);
            }
        }

        // ---------------------------------------------------------------------
        // Trade processing
        // ---------------------------------------------------------------------
        private void ProcessTrade(double price, double volume, DateTime time, double bid, double ask)
        {
            int direction = ClassifyTrade(price, bid, ask);
            int targetBar = ResolveTargetBar(time);

            if (State == State.Realtime)
                lastTickArrivalUtc = DateTime.UtcNow;

            if (hasOpenSweep && time == currentSweepTime && direction == currentSweepDirection)
            {
                currentSweepVolume       += volume;
                currentSweepVolumeXPrice += volume * price;
                if (price > currentSweepHigh) currentSweepHigh = price;
                if (price < currentSweepLow)  currentSweepLow  = price;
            }
            else
            {
                CloseCurrentSweep();

                hasOpenSweep             = true;
                currentSweepTime         = time;
                currentSweepDirection    = direction;
                currentSweepBar          = targetBar;
                currentSweepVolume       = volume;
                currentSweepVolumeXPrice = volume * price;
                currentSweepHigh         = price;
                currentSweepLow          = price;
            }

            // Every trade after a bubble counts toward that bubble's absorption window,
            // including the trade that closed the sweep above.
            if (ShowAbsorption)
                UpdateAbsorptionWatches(price, time, targetBar);
        }

        // Aggressor classification:
        //   1. Trade at/above the ask = buy, at/below the bid = sell, using the bid/ask
        //      stamped on the trade.
        //   2. Otherwise (inside the spread, locked/crossed or no quote) use the tick rule.
        private int ClassifyTrade(double price, double bid, double ask)
        {
            // Tick rule is always kept up to date so the fallback has continuity.
            int tickRuleDirection;
            if (!hasLastTick)
                tickRuleDirection = previousTickDirection;
            else if (price > lastTickPrice)
                tickRuleDirection = 1;
            else if (price < lastTickPrice)
                tickRuleDirection = -1;
            else
                tickRuleDirection = previousTickDirection;

            hasLastTick           = true;
            lastTickPrice         = price;
            previousTickDirection = tickRuleDirection;

            if (bid <= 0 || ask <= 0)
            {
                fallbackNoQuote++;
            }
            else
            {
                MasterInstrument mi = Instrument.MasterInstrument;
                if (mi.Compare(ask, bid) > 0) // valid, non-crossed quote
                {
                    if (mi.Compare(price, ask) >= 0) { tradesByQuote++; return 1; }
                    if (mi.Compare(price, bid) <= 0) { tradesByQuote++; return -1; }
                    fallbackInsideSpread++;
                }
                else
                {
                    fallbackLockedOrCrossed++;
                }
            }

            tradesByTickRule++;
            return tickRuleDirection;
        }

        private void PrintClassificationSummary()
        {
            long total = tradesByQuote + tradesByTickRule;
            if (total == 0) return;

            double quoteShare = (double)tradesByQuote / total;

            // Always shown: users need to know when buy/sell sides are only estimated.
            if (quoteShare < 0.05)
                Print(string.Format("icPABubbleSweep [{0}]: little or no bid/ask-stamped tick data was found. Your data provider may not supply it, so buy/sell sides were estimated with the tick rule and may be less accurate.",
                    Instrument.FullName));

            if (!PrintDiagnostics) return;

            Print(string.Format("icPABubbleSweep v{0} [{1}]: {2:N0} historical trades - {3:P1} classified by bid/ask, {4:P1} by tick rule.",
                IndicatorVersion, Instrument.FullName, total, quoteShare, 1 - quoteShare));

            Print(string.Format("  Fallback breakdown: no quote {0:N0} | locked/crossed quote {1:N0} | inside spread {2:N0}",
                fallbackNoQuote, fallbackLockedOrCrossed, fallbackInsideSpread));
            Print(string.Format("  Sweeps: {0:N0} total, {1:N0} shown as bubbles ({2:P3}) | historical processing time: {3:N1} s",
                sweepsTotal, sweepsRecorded, sweepsTotal > 0 ? (double)sweepsRecorded / sweepsTotal : 0,
                processingTimer != null ? processingTimer.Elapsed.TotalSeconds : 0));

            double threshold = CurrentThreshold();
            Print(string.Format("  Threshold now: {0} ({1}) | fixed threshold {2} = top {3:P3} of the last {4:N0} sweeps",
                double.IsInfinity(threshold) ? "warming up" : threshold.ToString("0.##"),
                UseAdaptiveThreshold ? AdaptivePercentile.ToString("0.###") + "th percentile" : "fixed",
                MinSweepVolume.ToString("0.##"), adaptiveWindow.ShareAtOrAbove(MinSweepVolume), adaptiveWindow.Count));

            if (ShowZones)
            {
                int active;
                lock (sweepLock) active = activeZones.Count;
                Print(string.Format("  Clustering zones: {0:N0} formed, {1:N0} still active", zonesCreated, active));
            }

            if (ShowAbsorption)
            {
                long judged = absorbedCount + notAbsorbedCount;
                Print(string.Format("  Absorption: {0:N0} of {1:N0} judged bubbles absorbed ({2:P1})",
                    absorbedCount, judged, judged > 0 ? (double)absorbedCount / judged : 0));
            }
        }

        // Works out which primary bar a trade belongs to.
        private int ResolveTargetBar(DateTime tradeTime)
        {
            int current = CurrentBars[0];
            int target;

            if (State == State.Realtime)
            {
                // The primary series has already been updated with this trade.
                target = current;
            }
            else if (tradeTime > Times[0][0])
            {
                target = current + 1;
            }
            else if (primaryIsTickBased && GetAttributed(current) >= primaryTicksPerBar)
            {
                target = current + 1;   // same timestamp, but the closed tick bar is already full
            }
            else
            {
                target = current;
            }

            if (primaryIsTickBased)
                ticksAttributed[target] = GetAttributed(target) + 1;

            return target;
        }

        private int GetAttributed(int bar)
        {
            int count;
            return ticksAttributed.TryGetValue(bar, out count) ? count : 0;
        }

        private void CloseCurrentSweep()
        {
            if (!hasOpenSweep) return;
            hasOpenSweep = false;
            sweepsTotal++;

            // Compare against the threshold from earlier sweeps, then add this one.
            double threshold = CurrentThreshold();
            UpdateAdaptiveWindow(currentSweepVolume);

            if (currentSweepVolume < threshold) return;

            RecordSweep(currentSweepBar, currentSweepDirection, currentSweepVolume);
            var record = new SweepRecord
            {
                Bar          = currentSweepBar,
                Price        = currentSweepVolumeXPrice / currentSweepVolume,
                Volume       = currentSweepVolume,
                Threshold    = threshold,
                Direction    = currentSweepDirection,
                Time         = currentSweepTime,
                High         = currentSweepHigh,
                Low          = currentSweepLow
            };
            // May fold into an existing bubble on the same bar; 'shown' is the drawn bubble.
            SweepRecord shown = AddOrMergeSweepRecord(record);
            sweepsRecorded++;

            if (ShowZones)
                ProcessClusterPoint(currentSweepBar, currentSweepTime, record.Price, currentSweepVolume, currentSweepDirection);

            if (ShowAbsorption)
            {
                absorptionWatches.Add(new AbsorptionWatch
                {
                    Record         = shown,
                    Direction      = currentSweepDirection,
                    ReferencePrice = currentSweepDirection == 1 ? currentSweepHigh : currentSweepLow,
                    Expiry         = currentSweepTime.AddSeconds(AbsorptionWindowSeconds)
                });
            }
        }

        // ---------------------------------------------------------------------
        // Absorption
        // ---------------------------------------------------------------------
        // Called for every trade. A watch ends as "not absorbed" as soon as price trades
        // beyond the reference by more than the allowed ticks, and as "absorbed" when its
        // window ends without that happening (provided some trading took place).
        private void UpdateAbsorptionWatches(double price, DateTime time, int bar)
        {
            if (absorptionWatches.Count == 0) return;

            double limit = (AbsorptionMaxTicks + 0.5) * Instrument.MasterInstrument.TickSize;

            for (int i = absorptionWatches.Count - 1; i >= 0; i--)
            {
                AbsorptionWatch w = absorptionWatches[i];

                if (time > w.Expiry)
                {
                    FinishWatch(i, w.TradesSeen > 0, bar);
                    continue;
                }

                w.TradesSeen++;
                bool continued = w.Direction == 1
                    ? price - w.ReferencePrice > limit
                    : w.ReferencePrice - price > limit;
                if (continued)
                    FinishWatch(i, false, bar);
            }
        }

        // Real time only: ends windows by the clock, so a quiet market still confirms.
        // NinjaTrader's clock is in the same time zone as trade timestamps and follows
        // Playback time, so a paused or accelerated replay behaves correctly.
        private bool ExpireAbsorptionWatchesByClock()
        {
            bool any = false;
            DateTime now = NinjaTrader.Core.Globals.Now;
            for (int i = absorptionWatches.Count - 1; i >= 0; i--)
            {
                AbsorptionWatch w = absorptionWatches[i];
                if (now <= w.Expiry) continue;
                FinishWatch(i, w.TradesSeen > 0, CurrentBars[0]);
                any = true;
            }
            return any;
        }

        private void FinishWatch(int index, bool absorbed, int confirmBar)
        {
            AbsorptionWatch w = absorptionWatches[index];
            absorptionWatches.RemoveAt(index);

            if (!absorbed)
            {
                notAbsorbedCount++;
                return;
            }

            absorbedCount++;
            lock (sweepLock) w.Record.Absorbed = true;

            BarSweepStats s = GetOrCreateStats(confirmBar);
            if (w.Direction == 1) s.BuyAbsorbed  = true;
            else                  s.SellAbsorbed = true;
            WriteStatsForBar(confirmBar);
        }

        private BarSweepStats GetOrCreateStats(int bar)
        {
            BarSweepStats s;
            if (!statsByBar.TryGetValue(bar, out s))
            {
                s = new BarSweepStats();
                statsByBar[bar] = s;
            }
            return s;
        }

        // ---------------------------------------------------------------------
        // Clustering zones
        // ---------------------------------------------------------------------
        private void ProcessClusterPoint(int bar, DateTime time, double price, double volume, int direction)
        {
            double tick = Instrument.MasterInstrument.TickSize;

            lock (sweepLock)
            {
                // 1. A bubble inside an active same-side zone strengthens that zone.
                for (int i = 0; i < activeZones.Count; i++)
                {
                    Zone z = activeZones[i];
                    if (z.Direction == direction && price >= z.Low - tick && price <= z.High + tick)
                    {
                        z.BubbleCount++;
                        z.TotalVolume += volume;
                        return;
                    }
                }
            }

            // 2. Otherwise remember it and look for a new cluster around it.
            List<ClusterPoint> points = direction == 1 ? recentBuyPoints : recentSellPoints;
            // The window is in time, so zones behave the same on tick, minute and range charts.
            DateTime windowStart = time.AddMinutes(-ZoneWindowMinutes);
            RemovePoints(points, windowStart, int.MinValue);
            points.Add(new ClusterPoint { Bar = bar, Time = time, Price = price, Volume = volume });

            double range = ZonePriceRangeTicks * tick;

            // Best price window [lo, lo + range] that contains this bubble.
            double bestLo = 0;
            int    bestCount = 0;
            for (int c = 0; c < points.Count; c++)
            {
                double lo = points[c].Price;
                if (lo > price || price - lo > range) continue;

                int count = 0;
                for (int k = 0; k < points.Count; k++)
                {
                    double pp = points[k].Price;
                    if (pp >= lo && pp <= lo + range) count++;
                }

                if (count > bestCount) { bestCount = count; bestLo = lo; }
            }

            if (bestCount < ZoneMinBubbles) return;

            // Build the zone from the members and consume them.
            var zone = new Zone
            {
                Direction  = direction,
                StartBar   = int.MaxValue,
                CreatedBar = bar,
                Low        = double.MaxValue,
                High       = double.MinValue
            };
            for (int i = points.Count - 1; i >= 0; i--)
            {
                ClusterPoint p = points[i];
                if (p.Price < bestLo || p.Price > bestLo + range) continue;

                zone.StartBar     = Math.Min(zone.StartBar, p.Bar);
                zone.Low          = Math.Min(zone.Low, p.Price);
                zone.High         = Math.Max(zone.High, p.Price);
                zone.BubbleCount++;
                zone.TotalVolume += p.Volume;
                points.RemoveAt(i);
            }

            // Give very tight clusters a visible minimum height of two ticks.
            if (zone.High - zone.Low < 2 * tick)
            {
                double mid = (zone.High + zone.Low) / 2;
                zone.Low   = mid - tick;
                zone.High  = mid + tick;
            }

            lock (sweepLock)
            {
                zones.Add(zone);
                activeZones.Add(zone);
                if (zones.Count > MaxStoredZones)
                    TrimEndedZones();
            }
            zonesCreated++;

            FlagZoneFormed(bar, direction);
        }

        // Removes points older than a time or before a bar, without allocating.
        private static void RemovePoints(List<ClusterPoint> points, DateTime olderThan, int beforeBar)
        {
            int write = 0;
            for (int read = 0; read < points.Count; read++)
            {
                ClusterPoint p = points[read];
                if (p.Time < olderThan || p.Bar < beforeBar) continue;
                points[write++] = p;
            }
            if (write < points.Count)
                points.RemoveRange(write, points.Count - write);
        }

        // Call inside sweepLock. Drops the oldest ended zones; active zones are always kept.
        private void TrimEndedZones()
        {
            int toRemove = zones.Count - (MaxStoredZones - ZoneTrimBatch);
            int write = 0;
            for (int read = 0; read < zones.Count; read++)
            {
                Zone z = zones[read];
                if (toRemove > 0 && z.EndBar >= 0) { toRemove--; continue; }
                zones[write++] = z;
            }
            if (write < zones.Count)
                zones.RemoveRange(write, zones.Count - write);
        }

        private void CheckZoneBreaks(int closedBar, double close)
        {
            lock (sweepLock)
            {
                for (int i = activeZones.Count - 1; i >= 0; i--)
                {
                    Zone z = activeZones[i];
                    if (closedBar <= z.CreatedBar) continue;

                    bool broken = z.Direction == 1 ? close < z.Low : close > z.High;
                    if (!broken) continue;

                    z.EndBar = closedBar;
                    activeZones.RemoveAt(i);
                }
            }
        }

        // Called on the first bar of a new session: zones from earlier sessions stop at the
        // previous session's last bar, and half-built clusters are discarded. Zones created
        // by trades already attributed to the new session are kept.
        private void EndSessionZones(int firstBarOfSession)
        {
            if (!ZonesExpireAtSessionEnd) return;

            lock (sweepLock)
            {
                for (int i = activeZones.Count - 1; i >= 0; i--)
                {
                    Zone z = activeZones[i];
                    if (z.CreatedBar >= firstBarOfSession) continue;
                    z.EndBar = firstBarOfSession - 1;
                    activeZones.RemoveAt(i);
                }
            }

            RemovePoints(recentBuyPoints,  DateTime.MinValue, firstBarOfSession);
            RemovePoints(recentSellPoints, DateTime.MinValue, firstBarOfSession);
        }

        private void FlagZoneFormed(int bar, int direction)
        {
            BarSweepStats s = GetOrCreateStats(bar);
            if (direction == 1) s.BuyZoneFormed  = true;
            else                s.SellZoneFormed = true;

            WriteStatsForBar(bar);
        }

        // Threshold a sweep must reach to be shown. In adaptive mode, nothing is shown
        // until enough sweeps have been seen to estimate the percentile.
        private double CurrentThreshold()
        {
            if (!UseAdaptiveThreshold)
                return MinSweepVolume;
            if (double.IsNaN(cachedAdaptiveThreshold))
                return double.PositiveInfinity;
            return Math.Max(cachedAdaptiveThreshold, AdaptiveMinimumVolume);
        }

        private void UpdateAdaptiveWindow(double volume)
        {
            adaptiveWindow.Add(volume);
            if (!UseAdaptiveThreshold || adaptiveWindow.Count < AdaptiveWarmupSweeps) return;

            sweepsSinceRecompute++;
            if (double.IsNaN(cachedAdaptiveThreshold) || sweepsSinceRecompute >= AdaptiveRecomputeEvery)
            {
                cachedAdaptiveThreshold = adaptiveWindow.Percentile(AdaptivePercentile);
                sweepsSinceRecompute    = 0;
            }
        }

        // Adds a bubble, or merges it into a same-side bubble on the same bar within the
        // merge distance (volume summed, price re-averaged, range extended). Returns the
        // bubble that will be drawn.
        private SweepRecord AddOrMergeSweepRecord(SweepRecord record)
        {
            lock (sweepLock)
            {
                if (MergeBubbles)
                {
                    double maxDistance = (MergeDistanceTicks + 0.5) * Instrument.MasterInstrument.TickSize;
                    for (int j = sweeps.Count - 1; j >= 0 && sweeps[j].Bar >= record.Bar; j--)
                    {
                        SweepRecord e = sweeps[j];
                        if (e.Bar != record.Bar || e.Direction != record.Direction) continue;
                        if (Math.Abs(e.Price - record.Price) > maxDistance) continue;

                        double total = e.Volume + record.Volume;
                        e.Price       = (e.Price * e.Volume + record.Price * record.Volume) / total;
                        e.Volume      = total;
                        e.High        = Math.Max(e.High, record.High);
                        e.Low         = Math.Min(e.Low, record.Low);
                        e.MergedCount++;
                        return e;
                    }
                }

                // Sweeps arrive almost always in bar order; insert to keep the list sorted.
                int i = sweeps.Count;
                while (i > 0 && sweeps[i - 1].Bar > record.Bar) i--;
                sweeps.Insert(i, record);

                // Memory cap: drop the oldest bubbles in one batch (list stays sorted).
                if (sweeps.Count > MaxStoredBubbles)
                    sweeps.RemoveRange(0, BubbleTrimBatch);

                return record;
            }
        }

        // ---------------------------------------------------------------------
        // Real-time flush: closes a sweep after a period of no trades,
        // so a large print is not held back in a quiet market.
        // ---------------------------------------------------------------------
        private void OnFlushTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (isTerminating) return;
            Action<object> action = flushAction;
            if (action == null) return;
            try
            {
                TriggerCustomEvent(action, 0, null);
            }
            catch (Exception)
            {
                // The indicator can be torn down between the check above and the call.
            }
        }

        private void FlushStaleSweep()
        {
            if (isTerminating || State != State.Realtime) return;

            bool changed = false;
            if (hasOpenSweep && (DateTime.UtcNow - lastTickArrivalUtc).TotalMilliseconds >= RealtimeFlushDelayMs)
            {
                CloseCurrentSweep();
                changed = true;
            }
            if (ShowAbsorption && ExpireAbsorptionWatchesByClock())
                changed = true;

            if (changed)
                ForceRefresh(); // no tick is coming to repaint the chart
        }

        // ---------------------------------------------------------------------
        // Per-bar statistics
        // ---------------------------------------------------------------------
        private void RecordSweep(int bar, int direction, double volume)
        {
            BarSweepStats s = GetOrCreateStats(bar);

            if (direction == 1)
            {
                s.BuyCount++;
                s.BuyVolume += volume;
                if (volume > s.LargestBuy) s.LargestBuy = volume;
            }
            else
            {
                s.SellCount++;
                s.SellVolume += volume;
                if (volume > s.LargestSell) s.LargestSell = volume;
            }

            // Write straight away if the bar already exists (always true in real time).
            WriteStatsForBar(bar);
        }

        private void WriteStatsForBar(int bar)
        {
            int barsAgo = CurrentBars[0] - bar;
            if (barsAgo < 0) return; // bar not formed yet; written when it is

            BarSweepStats s;
            statsByBar.TryGetValue(bar, out s);

            buySweepCount[barsAgo]      = s != null ? s.BuyCount    : 0;
            sellSweepCount[barsAgo]     = s != null ? s.SellCount   : 0;
            buySweepVolume[barsAgo]     = s != null ? s.BuyVolume   : 0;
            sellSweepVolume[barsAgo]    = s != null ? s.SellVolume  : 0;
            largestBuySweep[barsAgo]    = s != null ? s.LargestBuy  : 0;
            largestSellSweep[barsAgo]   = s != null ? s.LargestSell : 0;
            bullishSweepSignal[barsAgo] = (s != null && s.BuyCount  > 0) ? 1 : 0;
            bearishSweepSignal[barsAgo] = (s != null && s.SellCount > 0) ? 1 : 0;
            buyZoneSignal[barsAgo]      = (s != null && s.BuyZoneFormed)  ? 1 : 0;
            sellZoneSignal[barsAgo]     = (s != null && s.SellZoneFormed) ? 1 : 0;
            buyAbsorption[barsAgo]      = (s != null && s.BuyAbsorbed)    ? 1 : 0;
            sellAbsorption[barsAgo]     = (s != null && s.SellAbsorbed)   ? 1 : 0;
        }

        private void PruneOldState()
        {
            // Bar-indexed state is only needed for the last few bars.
            int cutoff = CurrentBars[0] - 5;
            PruneDictionary(statsByBar, cutoff);
            PruneDictionary(ticksAttributed, cutoff);
        }

        private void PruneDictionary<T>(Dictionary<int, T> dict, int cutoff)
        {
            if (dict.Count == 0) return;
            pruneKeys.Clear();
            foreach (int key in dict.Keys)
                if (key < cutoff)
                    pruneKeys.Add(key);
            for (int i = 0; i < pruneKeys.Count; i++)
                dict.Remove(pruneKeys[i]);
        }

        // ---------------------------------------------------------------------
        // Rendering
        // ---------------------------------------------------------------------
        public override void OnRenderTargetChanged()
        {
            DisposeDxBrushes();
            if (RenderTarget == null) return;

            // Read colours here rather than caching them earlier, so brushes are always
            // built from the current settings whenever the render target is created.
            Color buyColor        = BrushToColor(BuyBubbleColor,      Colors.SeaGreen);
            Color sellColor       = BrushToColor(SellBubbleColor,     Colors.Firebrick);
            Color textColor       = BrushToColor(TextColor,           Colors.Black);
            Color absorptionColor = BrushToColor(AbsorptionRingColor, Colors.DarkGoldenrod);

            float fillOpacity = CircleOpacity / 100f;
            dxBuyFill    = MakeDxBrush(buyColor,  fillOpacity);
            dxSellFill   = MakeDxBrush(sellColor, fillOpacity);
            dxBuyStroke  = MakeDxBrush(buyColor,  1f);
            dxSellStroke = MakeDxBrush(sellColor, 1f);
            dxText       = MakeDxBrush(textColor, 1f);

            float zoneOpacity = ZoneOpacity / 100f;
            dxBuyZone    = MakeDxBrush(buyColor,  zoneOpacity);
            dxSellZone   = MakeDxBrush(sellColor, zoneOpacity);
            dxAbsorption = MakeDxBrush(absorptionColor, 1f);

            dxTooltipBackground = MakeDxBrush(Color.FromRgb(28, 28, 32), 0.92f);
            dxTooltipText       = MakeDxBrush(Colors.White, 1f);

            EnsureTextFormats();
        }

        // Text formats do not depend on the render target, so they are created once.
        private void EnsureTextFormats()
        {
            if (ShowVolumeText && volumeTextFormats == null)
            {
                volumeTextFormats = new SharpDX.DirectWrite.TextFormat[MaxVolumeFont + 1];
                for (int size = MinVolumeFont; size <= MaxVolumeFont; size++)
                {
                    volumeTextFormats[size] = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                        SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, size)
                    {
                        TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center,
                        ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center,
                        WordWrapping       = SharpDX.DirectWrite.WordWrapping.NoWrap
                    };
                }
            }

            if (ShowTooltips && tooltipFormat == null)
            {
                tooltipFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory,
                    "Arial", SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, (float)TooltipFontSize)
                {
                    WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap
                };
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (sweeps == null || ChartBars == null || RenderTarget == null || dxBuyFill == null)
                return;

            int   fromIndex   = ChartBars.FromIndex;
            int   toIndex     = ChartBars.ToIndex;
            float baseOpacity = CircleOpacity / 100f;

            SharpDX.Direct2D1.AntialiasMode oldMode = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

            renderedBubbles.Clear();
            int hoveredIndex = -1;

            lock (sweepLock)
            {
                if (ShowZones)
                    RenderZones(chartControl, chartScale, fromIndex, toIndex);

                for (int i = FirstSweepAtOrAfter(fromIndex); i < sweeps.Count; i++)
                {
                    SweepRecord s = sweeps[i];
                    if (s.Bar > toIndex) break;

                    float x        = chartControl.GetXByBarIndex(ChartBars, s.Bar);
                    float y        = chartScale.GetYByValue(s.Price);
                    float diameter = GetDiameter(s.Volume, s.Threshold);
                    float radius   = diameter / 2f;
                    bool  isBuy    = s.Direction == 1;

                    // 0 for a bubble at the threshold, 1 for one at the maximum size. The curve
                    // (power 0.75) lifts mid-sized bubbles, since most sweeps sit near the threshold.
                    float strength = 0f;
                    if (IntensityBySize && MaxCircleSize > MinCircleSize)
                        strength = (float)Math.Pow(Math.Min(1f, Math.Max(0f,
                                       (diameter - MinCircleSize) / (MaxCircleSize - MinCircleSize))), 0.75);

                    float strokeWidth;
                    float fillOpacity;
                    if (UseOutlineOnly)
                    {
                        strokeWidth = 1.75f + 2.75f * strength;                        // 1.75 to 4.5 px
                        fillOpacity = IntensityBySize ? 0.05f + 0.30f * strength : 0f;  // 5% to 35%
                    }
                    else
                    {
                        strokeWidth = 1.25f + 1.5f * strength;                         // 1.25 to 2.75 px
                        fillOpacity = baseOpacity + (Math.Min(1f, baseOpacity * 2f) - baseOpacity) * strength;
                    }

                    var ellipse = new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), radius, radius);

                    if (fillOpacity > 0.01f)
                    {
                        SharpDX.Direct2D1.Brush fill = isBuy ? dxBuyFill : dxSellFill;
                        fill.Opacity = fillOpacity;
                        RenderTarget.FillEllipse(ellipse, fill);
                    }
                    RenderTarget.DrawEllipse(ellipse, isBuy ? dxBuyStroke : dxSellStroke, strokeWidth);

                    if (ShowAbsorption && s.Absorbed)
                    {
                        float ringRadius = radius + strokeWidth / 2f + 2.5f;
                        var ring = new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), ringRadius, ringRadius);
                        RenderTarget.DrawEllipse(ring, dxAbsorption, 2f);
                    }

                    if (ShowVolumeText)
                        DrawVolumeText(s.Volume, x, y, diameter);

                    renderedBubbles.Add(new RenderedBubble { X = x, Y = y, Radius = radius + strokeWidth / 2f, Record = s });
                    if (hoveredRecord != null && ReferenceEquals(hoveredRecord, s))
                        hoveredIndex = renderedBubbles.Count - 1;
                }

                if (ShowTooltips && hoveredIndex >= 0)
                    DrawTooltip(renderedBubbles[hoveredIndex]);
            }

            RenderTarget.AntialiasMode = oldMode;
        }

        // ---------------------------------------------------------------------
        // Tooltips
        // ---------------------------------------------------------------------
        // Subscriptions run on the chart's UI thread. The chart reference is kept so the
        // handlers can always be removed, even if ChartControl is null at termination.
        private void SubscribeMouse()
        {
            ChartControl chart = ChartControl;
            if (chart == null) return; // not on a chart (e.g. hosted by a strategy)

            chart.Dispatcher.InvokeAsync(() =>
            {
                if (isTerminating || mouseChart != null) return;
                chart.MouseMove  += OnChartMouseMove;
                chart.MouseLeave += OnChartMouseLeave;
                mouseChart = chart;
            });
        }

        private void UnsubscribeMouse()
        {
            ChartControl chart = mouseChart ?? ChartControl;
            if (chart == null) return;

            chart.Dispatcher.InvokeAsync(() =>
            {
                chart.MouseMove  -= OnChartMouseMove;
                chart.MouseLeave -= OnChartMouseLeave;
                mouseChart = null;
            });
        }

        // UI thread. Hit-tests against the bubbles drawn in the last render and repaints
        // only when the hovered bubble changes.
        private void OnChartMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!ShowTooltips || isTerminating || ChartPanel == null || ChartControl == null
                || ChartControl.PresentationSource == null)
                return;

            System.Windows.Point p = e.GetPosition(ChartPanel as IInputElement);
            double mx = ChartingExtensions.ConvertToHorizontalPixels(p.X, ChartControl.PresentationSource);
            double my = ChartingExtensions.ConvertToVerticalPixels(p.Y, ChartControl.PresentationSource);

            SweepRecord hit = null;
            for (int i = renderedBubbles.Count - 1; i >= 0; i--) // topmost first
            {
                RenderedBubble b = renderedBubbles[i];
                double dx = mx - b.X, dy = my - b.Y;
                if (dx * dx + dy * dy <= (b.Radius + 2) * (b.Radius + 2))
                {
                    hit = b.Record;
                    break;
                }
            }

            if (!ReferenceEquals(hit, hoveredRecord))
            {
                hoveredRecord = hit;
                ForceRefresh();
            }
        }

        private void OnChartMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (hoveredRecord == null) return;
            hoveredRecord = null;
            ForceRefresh();
        }

        // Call inside sweepLock. The tooltip text changes per bubble, so its TextLayout is
        // created per frame (only while a bubble is hovered) inside a using block.
        private void DrawTooltip(RenderedBubble b)
        {
            if (tooltipFormat == null) return;

            SweepRecord s  = b.Record;
            MasterInstrument mi = Instrument.MasterInstrument;

            string levels = mi.Compare(s.High, s.Low) == 0
                ? mi.FormatPrice(s.High)
                : mi.FormatPrice(s.Low) + " - " + mi.FormatPrice(s.High)
                  + "  (" + (int)Math.Round((s.High - s.Low) / mi.TickSize + 1) + " levels)";

            string text =
                (s.Direction == 1 ? "BUY " : "SELL ") +
                (s.MergedCount > 1 ? s.MergedCount + " sweeps merged  " : "sweep  ") +
                s.Volume.ToString("0.##") + "\n" +
                "Price: " + levels + "\n" +
                "Time: " + s.Time.ToString("HH:mm:ss.fff") + "\n" +
                "Threshold: " + s.Threshold.ToString("0.##") + "  (" + (s.Volume / s.Threshold).ToString("0.0") + "x)" +
                (s.Absorbed ? "\nAbsorbed" : "");

            using (var layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory,
                                    text, tooltipFormat, 1000f, 1000f))
            {
                float pad = Math.Max(6f, TooltipFontSize * 0.5f);
                float w = layout.Metrics.Width  + pad * 2;
                float h = layout.Metrics.Height + pad * 2;

                // Beside the bubble, flipped left or nudged up if it would leave the panel.
                float left = b.X + b.Radius + 8f;
                if (left + w > ChartPanel.X + ChartPanel.W) left = b.X - b.Radius - 8f - w;
                float top = b.Y - h / 2f;
                if (top < ChartPanel.Y) top = ChartPanel.Y;
                if (top + h > ChartPanel.Y + ChartPanel.H) top = ChartPanel.Y + ChartPanel.H - h;

                var rect = new SharpDX.RectangleF(left, top, w, h);
                RenderTarget.FillRectangle(rect, dxTooltipBackground);
                RenderTarget.DrawRectangle(rect, s.Direction == 1 ? dxBuyStroke : dxSellStroke, 1f);
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(left + pad, top + pad), layout, dxTooltipText);
            }
        }

        // Call inside sweepLock. Active zones extend to the right edge of the panel.
        private void RenderZones(ChartControl chartControl, ChartScale chartScale, int fromIndex, int toIndex)
        {
            float halfBar    = chartControl.Properties.BarDistance / 2f;
            float panelRight = ChartPanel.X + ChartPanel.W;

            for (int i = 0; i < zones.Count; i++)
            {
                Zone z = zones[i];
                bool active = z.EndBar < 0;
                if (!active && !ShowBrokenZones) continue;
                if (z.StartBar > toIndex) continue;
                if (!active && z.EndBar < fromIndex) continue;

                float left   = chartControl.GetXByBarIndex(ChartBars, z.StartBar) - halfBar;
                float right  = active ? panelRight : chartControl.GetXByBarIndex(ChartBars, z.EndBar) + halfBar;
                float top    = chartScale.GetYByValue(z.High);
                float bottom = chartScale.GetYByValue(z.Low);
                if (right <= left) continue;

                var rect = new SharpDX.RectangleF(left, top, right - left, Math.Max(1f, bottom - top));
                bool isBuy = z.Direction == 1;
                RenderTarget.FillRectangle(rect, isBuy ? dxBuyZone : dxSellZone);
                RenderTarget.DrawRectangle(rect, isBuy ? dxBuyStroke : dxSellStroke, 1f);
            }
        }

        // The volume text changes per bubble, so its TextLayout is created per frame inside a
        // using block (only when Show Volume Text is on).
        private void DrawVolumeText(double volume, float x, float y, float diameter)
        {
            if (volumeTextFormats == null) return;

            int fontSize = (int)Math.Round(diameter * 0.32);
            if (fontSize < MinVolumeFont) fontSize = MinVolumeFont;
            if (fontSize > MaxVolumeFont) fontSize = MaxVolumeFont;

            SharpDX.DirectWrite.TextFormat format = volumeTextFormats[fontSize];
            if (format == null) return;

            const float boxWidth = 200f;
            float boxHeight = fontSize * 2f;
            using (var layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory,
                                    volume.ToString("0.##"), format, boxWidth, boxHeight))
            {
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x - boxWidth / 2f, y - boxHeight / 2f), layout, dxText);
            }
        }

        // Area grows with volume: diameter = base * sqrt(volume / threshold), capped.
        private float GetDiameter(double volume, double threshold)
        {
            double d = MinCircleSize * Math.Sqrt(volume / threshold);
            if (d > MaxCircleSize) d = MaxCircleSize;
            if (d < MinCircleSize) d = MinCircleSize;
            return (float)d;
        }

        // Binary search: index of the first sweep with Bar >= bar. Call inside sweepLock.
        private int FirstSweepAtOrAfter(int bar)
        {
            int lo = 0, hi = sweeps.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sweeps[mid].Bar < bar) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private SharpDX.Direct2D1.Brush MakeDxBrush(Color color, float opacity)
        {
            var brush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                new SharpDX.Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f));
            brush.Opacity = opacity;
            return brush;
        }

        private void DisposeDxBrushes()
        {
            if (dxBuyFill    != null) { dxBuyFill.Dispose();    dxBuyFill    = null; }
            if (dxSellFill   != null) { dxSellFill.Dispose();   dxSellFill   = null; }
            if (dxBuyStroke  != null) { dxBuyStroke.Dispose();  dxBuyStroke  = null; }
            if (dxSellStroke != null) { dxSellStroke.Dispose(); dxSellStroke = null; }
            if (dxText       != null) { dxText.Dispose();       dxText       = null; }
            if (dxBuyZone    != null) { dxBuyZone.Dispose();    dxBuyZone    = null; }
            if (dxSellZone   != null) { dxSellZone.Dispose();   dxSellZone   = null; }
            if (dxAbsorption != null) { dxAbsorption.Dispose(); dxAbsorption = null; }
            if (dxTooltipBackground != null) { dxTooltipBackground.Dispose(); dxTooltipBackground = null; }
            if (dxTooltipText       != null) { dxTooltipText.Dispose();       dxTooltipText       = null; }
        }

        private void DisposeTextFormats()
        {
            if (tooltipFormat != null) { tooltipFormat.Dispose(); tooltipFormat = null; }
            if (volumeTextFormats != null)
            {
                for (int i = 0; i < volumeTextFormats.Length; i++)
                    if (volumeTextFormats[i] != null) volumeTextFormats[i].Dispose();
                volumeTextFormats = null;
            }
        }

        private static Color BrushToColor(Brush brush, Color fallback)
        {
            var solid = brush as SolidColorBrush;
            return solid != null ? solid.Color : fallback;
        }

        #region Output series (for strategies / other indicators)

        [Browsable(false)] [XmlIgnore]
        public Series<double> BuySweepCount      { get { Update(); return buySweepCount; } }

        [Browsable(false)] [XmlIgnore]
        public Series<double> SellSweepCount     { get { Update(); return sellSweepCount; } }

        [Browsable(false)] [XmlIgnore]
        public Series<double> BuySweepVolume     { get { Update(); return buySweepVolume; } }

        [Browsable(false)] [XmlIgnore]
        public Series<double> SellSweepVolume    { get { Update(); return sellSweepVolume; } }

        [Browsable(false)] [XmlIgnore]
        public Series<double> LargestBuySweep    { get { Update(); return largestBuySweep; } }

        [Browsable(false)] [XmlIgnore]
        public Series<double> LargestSellSweep   { get { Update(); return largestSellSweep; } }

        /// <summary>1 if the bar had at least one buy sweep, else 0.</summary>
        [Browsable(false)] [XmlIgnore]
        public Series<double> BullishSweepSignal { get { Update(); return bullishSweepSignal; } }

        /// <summary>1 if the bar had at least one sell sweep, else 0.</summary>
        [Browsable(false)] [XmlIgnore]
        public Series<double> BearishSweepSignal { get { Update(); return bearishSweepSignal; } }

        /// <summary>Volume threshold in effect on the bar (0 while the adaptive threshold warms up).</summary>
        [Browsable(false)] [XmlIgnore]
        public Series<double> SweepThreshold     { get { Update(); return sweepThreshold; } }

        /// <summary>1 on the bar a buy (support) clustering zone forms, else 0.</summary>
        [Browsable(false)] [XmlIgnore]
        public Series<double> BuyZoneSignal      { get { Update(); return buyZoneSignal; } }

        /// <summary>1 on the bar a sell (resistance) clustering zone forms, else 0.</summary>
        [Browsable(false)] [XmlIgnore]
        public Series<double> SellZoneSignal     { get { Update(); return sellZoneSignal; } }

        /// <summary>1 on the bar where a buy sweep's absorption is confirmed, else 0.</summary>
        [Browsable(false)] [XmlIgnore]
        public Series<double> BuyAbsorption      { get { Update(); return buyAbsorption; } }

        /// <summary>1 on the bar where a sell sweep's absorption is confirmed, else 0.</summary>
        [Browsable(false)] [XmlIgnore]
        public Series<double> SellAbsorption     { get { Update(); return sellAbsorption; } }

        #endregion

        #region Properties

        // Properties are declared in display order (group, then Order), except that the three
        // [NinjaScriptProperty] inputs come first: their declaration order fixes the constructor
        // signature (minSweepVolume, useAdaptiveThreshold, adaptivePercentile).

        // ---- 1. General ----

        [NinjaScriptProperty]
        [Range(0.000001, double.MaxValue)]
        [Display(Name = "Fixed Minimum Sweep Volume", Description = "Minimum total volume of a sweep when the adaptive threshold is off.", Order = 5, GroupName = "1. General")]
        public double MinSweepVolume { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Adaptive Threshold", Description = "Show sweeps in the top percentile of recent sweep sizes, so the indicator adapts to any instrument and session. Turn off to use the fixed volume.", Order = 1, GroupName = "1. General")]
        public bool UseAdaptiveThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(50, 99.999)]
        [Display(Name = "Adaptive Percentile", Description = "Higher shows fewer, larger sweeps. 99.95 = the largest 0.05% of recent sweeps.", Order = 2, GroupName = "1. General")]
        public double AdaptivePercentile { get; set; }

        [Range(1000, 1000000)]
        [Display(Name = "Adaptive Lookback (sweeps)", Description = "Number of recent sweeps the percentile is calculated over.", Order = 3, GroupName = "1. General")]
        public int AdaptiveLookback { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Adaptive Minimum Volume", Description = "The adaptive threshold never goes below this volume (0 = no floor).", Order = 4, GroupName = "1. General")]
        public double AdaptiveMinimumVolume { get; set; }

        [Range(50, 5000)]
        [Display(Name = "Real-time Flush Delay (ms)", Description = "In real time, a sweep is closed and drawn after this many milliseconds without a new trade.", Order = 6, GroupName = "1. General")]
        public int RealtimeFlushDelayMs { get; set; }

        // ---- 2. Visuals ----

        [Display(Name = "Use Outline Only", Description = "Draws an empty ring instead of a filled bubble.", Order = 1, GroupName = "2. Visuals")]
        public bool UseOutlineOnly { get; set; }

        [Range(4, 200)]
        [Display(Name = "Base Bubble Diameter (px)", Description = "Diameter of a bubble whose volume exactly equals the threshold. Bubble area grows in proportion to volume above it.", Order = 2, GroupName = "2. Visuals")]
        public int MinCircleSize { get; set; }

        [Range(10, 500)]
        [Display(Name = "Maximum Bubble Diameter (px)", Description = "Largest bubble size; very large sweeps are capped here. Never smaller than the base diameter.", Order = 3, GroupName = "2. Visuals")]
        public int MaxCircleSize { get; set; }

        [Range(1, 100)]
        [Display(Name = "Fill Opacity (%)", Description = "Fill transparency of the smallest bubbles; larger ones get more opaque. Only applies when 'Use Outline Only' is off.", Order = 4, GroupName = "2. Visuals")]
        public int CircleOpacity { get; set; }

        [Display(Name = "Intensity By Size", Description = "Larger sweeps get a thicker outline and stronger fill, so the biggest stand out.", Order = 5, GroupName = "2. Visuals")]
        public bool IntensityBySize { get; set; }

        [XmlIgnore]
        [Display(Name = "Buy Bubble Color", Description = "Color for buy sweeps and buy (support) zones.", Order = 6, GroupName = "2. Visuals")]
        public Brush BuyBubbleColor { get; set; }

        [Browsable(false)]
        public string BuyBubbleColorSerialize
        {
            get { return Serialize.BrushToString(BuyBubbleColor); }
            set { BuyBubbleColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Sell Bubble Color", Description = "Color for sell sweeps and sell (resistance) zones.", Order = 7, GroupName = "2. Visuals")]
        public Brush SellBubbleColor { get; set; }

        [Browsable(false)]
        public string SellBubbleColorSerialize
        {
            get { return Serialize.BrushToString(SellBubbleColor); }
            set { SellBubbleColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Show Volume Text", Description = "Print each sweep's volume inside its bubble.", Order = 8, GroupName = "2. Visuals")]
        public bool ShowVolumeText { get; set; }

        [XmlIgnore]
        [Display(Name = "Volume Text Color", Description = "Color of the volume text inside bubbles.", Order = 9, GroupName = "2. Visuals")]
        public Brush TextColor { get; set; }

        [Browsable(false)]
        public string TextColorSerialize
        {
            get { return Serialize.BrushToString(TextColor); }
            set { TextColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Merge Bubbles Within Bar", Description = "Combine same-side bubbles on the same bar that are within the merge distance into one bubble with their total volume. Keeps higher timeframes readable.", Order = 10, GroupName = "2. Visuals")]
        public bool MergeBubbles { get; set; }

        [Range(0, 100)]
        [Display(Name = "Merge Distance (ticks)", Description = "Maximum price distance between bubbles that are merged. 0 = same price only.", Order = 11, GroupName = "2. Visuals")]
        public int MergeDistanceTicks { get; set; }

        [Display(Name = "Show Tooltips", Description = "Hover a bubble to see its volume, price levels, time, threshold and absorption.", Order = 12, GroupName = "2. Visuals")]
        public bool ShowTooltips { get; set; }

        [Range(8, 36)]
        [Display(Name = "Tooltip Font Size", Description = "Text size of the hover tooltip.", Order = 13, GroupName = "2. Visuals")]
        public int TooltipFontSize { get; set; }

        // ---- 3. Clustering Zones ----

        [Display(Name = "Show Clustering Zones", Description = "Draw a zone where several same-side bubbles cluster in price.", Order = 1, GroupName = "3. Clustering Zones")]
        public bool ShowZones { get; set; }

        [Range(2, 50)]
        [Display(Name = "Minimum Bubbles", Description = "Same-side bubbles needed to form a zone.", Order = 2, GroupName = "3. Clustering Zones")]
        public int ZoneMinBubbles { get; set; }

        [Range(1, 1000)]
        [Display(Name = "Price Range (ticks)", Description = "Maximum distance between the highest and lowest bubble in a cluster.", Order = 3, GroupName = "3. Clustering Zones")]
        public int ZonePriceRangeTicks { get; set; }

        [Range(0.5, 240)]
        [Display(Name = "Time Window (minutes)", Description = "The clustered bubbles must fall within this many minutes. Time-based, so zones behave the same on any chart type.", Order = 4, GroupName = "3. Clustering Zones")]
        public double ZoneWindowMinutes { get; set; }

        [Display(Name = "Expire At Session End", Description = "Zones still active when the trading session ends stop at the session's last bar.", Order = 5, GroupName = "3. Clustering Zones")]
        public bool ZonesExpireAtSessionEnd { get; set; }

        [Display(Name = "Show Broken Zones", Description = "Keep ended zones visible (stopping at the bar that broke them, or at the session end).", Order = 6, GroupName = "3. Clustering Zones")]
        public bool ShowBrokenZones { get; set; }

        [Range(1, 100)]
        [Display(Name = "Zone Opacity (%)", Description = "Fill transparency of clustering zones.", Order = 7, GroupName = "3. Clustering Zones")]
        public int ZoneOpacity { get; set; }

        // ---- 4. Absorption ----

        [Display(Name = "Show Absorption", Description = "Mark bubbles whose sweep failed to move price further in its direction.", Order = 1, GroupName = "4. Absorption")]
        public bool ShowAbsorption { get; set; }

        [Range(0.5, 300)]
        [Display(Name = "Window (seconds)", Description = "How long after the sweep price is watched.", Order = 2, GroupName = "4. Absorption")]
        public double AbsorptionWindowSeconds { get; set; }

        [Range(0, 100)]
        [Display(Name = "Max Continuation (ticks)", Description = "If price trades more than this many ticks beyond the sweep's extreme within the window, it is not absorption. 0 = any trade beyond the extreme cancels it (strictest).", Order = 3, GroupName = "4. Absorption")]
        public int AbsorptionMaxTicks { get; set; }

        [XmlIgnore]
        [Display(Name = "Ring Color", Description = "Color of the ring drawn around absorbed bubbles.", Order = 4, GroupName = "4. Absorption")]
        public Brush AbsorptionRingColor { get; set; }

        [Browsable(false)]
        public string AbsorptionRingColorSerialize
        {
            get { return Serialize.BrushToString(AbsorptionRingColor); }
            set { AbsorptionRingColor = Serialize.StringToBrush(value); }
        }

        // ---- 5. Diagnostics ----

        [Display(Name = "Print Diagnostics", Description = "Prints classification, threshold, zone, absorption and performance details to the NinjaScript Output window when the indicator goes real time. Useful for tuning.", Order = 1, GroupName = "5. Diagnostics")]
        public bool PrintDiagnostics { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private icPA.icPABubbleSweep[] cacheicPABubbleSweep;
		public icPA.icPABubbleSweep icPABubbleSweep(double minSweepVolume, bool useAdaptiveThreshold, double adaptivePercentile)
		{
			return icPABubbleSweep(Input, minSweepVolume, useAdaptiveThreshold, adaptivePercentile);
		}

		public icPA.icPABubbleSweep icPABubbleSweep(ISeries<double> input, double minSweepVolume, bool useAdaptiveThreshold, double adaptivePercentile)
		{
			if (cacheicPABubbleSweep != null)
				for (int idx = 0; idx < cacheicPABubbleSweep.Length; idx++)
					if (cacheicPABubbleSweep[idx] != null && cacheicPABubbleSweep[idx].MinSweepVolume == minSweepVolume && cacheicPABubbleSweep[idx].UseAdaptiveThreshold == useAdaptiveThreshold && cacheicPABubbleSweep[idx].AdaptivePercentile == adaptivePercentile && cacheicPABubbleSweep[idx].EqualsInput(input))
						return cacheicPABubbleSweep[idx];
			return CacheIndicator<icPA.icPABubbleSweep>(new icPA.icPABubbleSweep(){ MinSweepVolume = minSweepVolume, UseAdaptiveThreshold = useAdaptiveThreshold, AdaptivePercentile = adaptivePercentile }, input, ref cacheicPABubbleSweep);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.icPA.icPABubbleSweep icPABubbleSweep(double minSweepVolume, bool useAdaptiveThreshold, double adaptivePercentile)
		{
			return indicator.icPABubbleSweep(Input, minSweepVolume, useAdaptiveThreshold, adaptivePercentile);
		}

		public Indicators.icPA.icPABubbleSweep icPABubbleSweep(ISeries<double> input , double minSweepVolume, bool useAdaptiveThreshold, double adaptivePercentile)
		{
			return indicator.icPABubbleSweep(input, minSweepVolume, useAdaptiveThreshold, adaptivePercentile);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.icPA.icPABubbleSweep icPABubbleSweep(double minSweepVolume, bool useAdaptiveThreshold, double adaptivePercentile)
		{
			return indicator.icPABubbleSweep(Input, minSweepVolume, useAdaptiveThreshold, adaptivePercentile);
		}

		public Indicators.icPA.icPABubbleSweep icPABubbleSweep(ISeries<double> input , double minSweepVolume, bool useAdaptiveThreshold, double adaptivePercentile)
		{
			return indicator.icPABubbleSweep(input, minSweepVolume, useAdaptiveThreshold, adaptivePercentile);
		}
	}
}

#endregion