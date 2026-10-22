using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using DesktopSplitter.Services;
using DesktopSplitter.ViewModels;
using LayoutKind = DesktopSplitter.Models.LayoutKind;

internal static class FreeLayoutChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static void Run()
    {
        Check(Marshal.SizeOf<DESKSPLIT_CONFIG>() == 944 && DriverClient.NewConfig().Monitors.Length == 9
            && DriverClient.DESKSPLIT_PROTOCOL_VERSION == 3, "Nine-output driver ABI mismatch");
        LayoutCut[] grid = [new(0,true,1.0/3),new(1,true,0.5),new(0,false,1.0/3),new(1,false,0.5),
            new(3,false,1.0/3),new(4,false,0.5),new(6,false,1.0/3),new(7,false,0.5)];
        var layout = LayoutDefinition.CreateCustom(grid);
        var nine = layout.ComputePhysRects(1920,1080);
        Check(nine.Count == 9 && nine.All(r => r.W == 640 && r.H == 360), "FHD nine-way split rejected or distorted");
        var physical = new MonitorInfo { DeviceName="P", MonitorDevicePath="FREE-FIXTURE",CurrentWidth=1920,CurrentHeight=1080 };
        var virtuals = Enumerable.Range(0,9).Select(i => new MonitorInfo { DeviceName=$"V{i}", IsDeskSplitVirtual=true,DeskSplitMonitorIndex=i }).ToArray();
        var sizes = layout.ComputeDefaultSegmentSizes(1920,1080);
        ApplyService.ValidateRequest(new ApplyRequest { Physical=physical, Layout=layout, Resolutions=sizes, PrimarySlot=9 });
        var placements=DisplayArranger.ComputePlacements(physical,layout,virtuals,sizes);
        Check(placements.Select((p,i) => p.X == 1920+nine[i].X && p.Y == nine[i].Y).All(ok=>ok), "Desktop positions diverged from free cuts");
        var rightMonitor=physical with {DeviceName="OTHER-RIGHT",MonitorDevicePath="OTHER-RIGHT",PositionX=1920};
        var leftMonitor=physical with {DeviceName="OTHER-LEFT",MonitorDevicePath="OTHER-LEFT",PositionX=-1920};
        var multi=DisplayArranger.ComposeLayout(new[] {physical,rightMonitor,leftMonitor},placements,60,"V0");
        foreach(var splitMonitor in multi.Where(m=>m.DeviceName.StartsWith("V")))
            foreach(var real in multi.Where(m=>!m.DeviceName.StartsWith("V")))
                Check(!(splitMonitor.X<real.Right && splitMonitor.Right>real.X && splitMonitor.Y<real.Bottom && splitMonitor.Bottom>real.Y),"Capture desktop overlaps another physical monitor");
        Check(multi.Single(m=>m.DeviceName=="OTHER-RIGHT").X-multi.Single(m=>m.DeviceName=="P").X==1920
            && multi.Single(m=>m.DeviceName=="OTHER-LEFT").X-multi.Single(m=>m.DeviceName=="P").X == -1920,"Unrelated physical monitor arrangement was changed");
        var persisted = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings {
            Layout=LayoutKind.Custom, CustomLayoutCuts=grid.ToList(), Segments=sizes.Select(s=>new SegmentSize { Width=s.Width,Height=s.Height }).ToList() }))!;
        var restored = LayoutDefinition.FromKind(persisted.Layout,persisted.CustomLayoutCuts);
        Check(restored.ComputePhysRects(1920,1080).SequenceEqual(nine), "Custom shape lost across persistence");
        var rollback = new AppliedSnapshot(true,"P",LayoutKind.Custom,sizes,false) { CustomCuts=grid };
        Check(LayoutDefinition.FromKind(rollback.Layout,rollback.CustomCuts).ComputePhysRects(1920,1080).SequenceEqual(nine), "Rollback lost cut geometry");
        Check(ApplyService.ResolveSnapshotLayout(rollback,physical) is not null
            && ApplyService.ResolveSnapshotLayout(rollback,physical with { CurrentWidth=2560 }) is null
            && ApplyService.ResolveSnapshotLayout(rollback with { CustomCuts=[new(7,true,.5)] },physical) is null,
            "Rollback did not reject resized or corrupt free-layout snapshots before changing displays");
        Check(SplitDisplayReorder.Encode(Enumerable.Range(0,9).ToArray()) == 0x4453009876543210UL, "Native nine-route encoding differs");
        var routedSnapshot = rollback with { RoutingOrder=Enumerable.Range(0,9).Reverse().ToArray() };
        var identifiedSnapshot=routedSnapshot with {PhysicalMonitorPath=physical.MonitorDevicePath};
        Check(ApplyService.ResolveSnapshotLayout(identifiedSnapshot,physical with {MonitorDevicePath="other-monitor"}) is null
            && ApplyService.ResolveSnapshotLayout(identifiedSnapshot,physical with {DeviceName="RENAMED"}) is not null,
            "Rollback accepted a reused device name or rejected a stable monitor identity");
        int restores=0;
        ApplyService.RestoreSnapshotRouting(routedSnapshot,order=>{
            restores++; Check(order.SequenceEqual(Enumerable.Range(0,9).Reverse()), "Rollback lost the nine-slot order");
        });
        Check(restores==1 && ApplyService.ResolveSnapshotLayout(routedSnapshot,physical) is not null, "Rollback ignored swapped numbering");
        ApplyService.RestoreSnapshotRouting(rollback with { RoutingOrder=Enumerable.Range(0,9).ToArray() },_=>throw new Exception("Identity caused extra display changes"));
        Check(ApplyService.ResolveSnapshotLayout(routedSnapshot with { RoutingOrder=[0,1] },physical) is null
            && ApplyService.ResolveSnapshotLayout(routedSnapshot with { RoutingOrder=new int[9] },physical) is null, "Invalid rollback route was accepted");
        var config=new CompositorConfig {PhysicalDevice="P", Segments=Enumerable.Range(0,9).Select(i=>new CompositorSegment { VirtualDevice=$"V{i}" }).ToList()};
        var targets=Enumerable.Range(0,9).Select(i=>new SplitTarget(i+1,$"V{i}",new(0,0,640,360),new(0,0,640,360))).ToArray();
        var mapped=SplitDisplayReorder.MapTargets(targets,config,routedSnapshot.RoutingOrder!);
        Check(mapped[0].DeviceName=="V8" && mapped[8].DeviceName=="V0", "Slot numbering did not follow routing");
        try { SplitDisplayReorder.MapTargets(targets.Append(targets[0]).ToArray(),config,Enumerable.Range(0,9).ToArray()); throw new Exception("Extra virtual monitor was silently ignored"); } catch(InvalidOperationException) { }
        config.Segments[8].VirtualDevice="V0";
        try { SplitDisplayReorder.MapTargets(targets,config,Enumerable.Range(0,9).ToArray()); throw new Exception("Duplicate source mapped to multiple slots"); } catch(InvalidOperationException) { }
        Check(MatrixWorkspace.MarkerNameFor("P",LayoutKind.Custom,4) != MatrixWorkspace.MarkerNameFor("P",LayoutKind.Custom,9),
            "Evicting one custom slot-count profile could invalidate another profile's window markers");
        var rng=new Random(98273);
        for(int sample=0;sample<300;sample++) {
            var cuts=new List<LayoutCut>();
            for(int i=0;i<8;i++) cuts.Add(new(rng.Next(i+1),rng.Next(2)==0,0.2+rng.NextDouble()*0.6));
            var def=LayoutDefinition.CreateCustom(cuts);
            foreach(var (w,h) in new[] {(1920,1080),(1366,768),(3841,2161)}) {
                var rects=def.ComputePhysRects(w,h);
                Check(rects.Count==9 && rects.All(r=>r.X>=0 && r.Y>=0 && r.W>0 && r.H>0 && r.Right<=w && r.Bottom<=h)
                    && rects.Sum(r=>(long)r.W*r.H)==(long)w*h,"Random free layout left gaps or pixels out of bounds");
                for(int i=0;i<9;i++) for(int j=i+1;j<9;j++)
                    Check(!(rects[i].X<rects[j].Right && rects[j].X<rects[i].Right && rects[i].Y<rects[j].Bottom && rects[j].Y<rects[i].Bottom),"Random free layout overlapped");
            }
            var order=Enumerable.Range(0,9).OrderBy(_=>rng.Next()).ToArray();
            Check(SplitDisplayReorder.Decode(SplitDisplayReorder.Encode(order)).SequenceEqual(order),"Nine-slot routing was truncated");
        }
        foreach(var bad in new[] {new LayoutCut(1,true,.5),new LayoutCut(0,true,0),new LayoutCut(0,true,double.NaN)}) {
            try { LayoutDefinition.CreateCustom([bad]); throw new Exception("Malformed cut accepted"); } catch(ArgumentException) { }
        }
        try { LayoutDefinition.CreateCustom(grid.Append(new(0,true,.5)).ToArray()); throw new Exception("Driver capacity overflow accepted"); } catch(ArgumentException) { }
        var app=new DesktopSplitter.App(suppressStartup:true); app.InitializeComponent();
        string? before=File.Exists(SettingsStore.FilePath)?File.ReadAllText(SettingsStore.FilePath):null;
        var snapshot=new DesktopSnapshot(new[] {physical},"fixture","fixture");
        var view=new MainViewModel(previewOnly:true,statusReader:()=>snapshot);
        Program.CompleteOnDispatcher(view.Initialization);
        var window=new DesktopSplitter.MainWindow(view,null);
        view.SelectedLayout=LayoutDefinition.CreateCustom([]);
        foreach(var cut in grid) {
            var parent=FreeLayout.Compute(view.SelectedLayout!.CustomCuts,1920,1080).Rects[cut.Slot];
            (cut.Vertical?view.CutVerticalCommand:view.CutHorizontalCommand).Execute(null);
            double x=(parent.X+parent.W*(cut.Vertical?cut.Ratio:.5))*view.CanvasWidth/1920;
            double y=(parent.Y+parent.H*(cut.Vertical?.5:cut.Ratio))*view.CanvasHeight/1080;
            view.CutAt(cut.Slot,x,y);
        }
        Check(view.Segments.Count==9 && view.Zones.Count==9 && view.Splitters.Count==8 && !view.CutVerticalCommand.CanExecute(null),"Free editor did not reach nine slots");
        var first=view.Splitters[0];
        view.BeginSplitterDrag(0,first.X+SplitterVisual.Thickness/2,first.Y+10);
        view.UpdateSplitterDrag(view.CanvasWidth*.4,first.Y+10); view.EndSplitterDrag();
        Check(view.SelectedLayout!.CustomCuts[0].Ratio==.4 && view.Segments.Count==9,"Root boundary drag lost nested cuts");
        Program.CompleteOnDispatcher(view.RefreshMonitorsAsync());
        Check(view.Segments.Count==9 && window.FindName("LiveSlotsList") is null && ((FrameworkElement)window.FindName("SplitEditorContent")).Visibility==Visibility.Visible,"Refresh hid or reset the draft editor");
        var output=Path.GetFullPath("artifacts/ui-smoke-free");Directory.CreateDirectory(output);
        Program.Render(window,Path.Combine(output,"nine-custom.png"));
        view.UndoCutCommand.Execute(null);Check(view.Segments.Count==8,"Undo did not remove the last cut");
        view.ResetSegmentsCommand.Execute(null);Check(view.Segments.Count==1 && view.Splitters.Count==0,"Free reset kept old cuts");
        // Valid on FHD, but a nested two-pixel parent shrinks to one pixel on the other monitor.
        var tinyCuts = Enumerable.Range(0,8).Select(_=>new LayoutCut(0,true,.5)).ToArray();
        view.SelectedLayout=LayoutDefinition.CreateCustom(tinyCuts);
        var tinySplitter=view.Splitters[0];
        view.BeginSplitterDrag(0,tinySplitter.X+SplitterVisual.Thickness/2,tinySplitter.Y+10);
        view.SelectedMonitor=physical with { CurrentWidth=128,CurrentHeight=720 };
        Check(!view.IsDragging, "Monitor change kept a stale drag active");
        Check(view.Segments.Count==0 && view.Zones.Count==0 && !view.CutVerticalCommand.CanExecute(null)
            && view.SumNote.Contains("현재 모니터"), "Resolution change did not contain invalid nested pixel geometry");
        view.UndoCutCommand.Execute(null);
        Check(view.SelectedLayout!.CustomCuts.Count==7 && view.Segments.Count==8, "Undo could not recover an unrenderable draft");
        view.SelectedMonitor=physical;
        view.SelectedLayout=LayoutDefinition.CreateCustom(tinyCuts);
        view.SelectedMonitor=physical with { CurrentWidth=32,CurrentHeight=720 };
        view.UndoCutCommand.Execute(null);
        Check(view.SelectedLayout!.CustomCuts.Count==7 && view.Segments.Count==0, "Undo dropped invalid draft history");
        view.ResetSegmentsCommand.Execute(null);
        Check(view.Segments.Count==1 && view.SumNote=="", "Reset could not recover an unrenderable draft");
        view.SelectedMonitor=physical;
        view.SelectedLayout=LayoutDefinition.FromKind(LayoutKind.Grid3x3);
        var gridSplitter=view.Splitters[0];
        view.BeginSplitterDrag(0,gridSplitter.X+SplitterVisual.Thickness/2,gridSplitter.Y+10);
        view.SelectedLayout=LayoutDefinition.FromKind(LayoutKind.Grid2x2);
        var fourSizes=view.Segments.Select(segment=>(segment.Width,segment.Height)).ToArray();
        view.UpdateSplitterDrag(10,10);
        Check(!view.IsDragging && view.Segments.Select(segment=>(segment.Width,segment.Height)).SequenceEqual(fourSizes), "Stale nine-slot drag changed a four-slot draft");
        Check(before==(File.Exists(SettingsStore.FilePath)?File.ReadAllText(SettingsStore.FilePath):null),"Draft editing wrote user settings");
        window.Close();
        Console.WriteLine("PASS free layout: 900 nine-region geometry cases, 300 routing permutations, 944-byte ABI, persistence/rollback, pointer cuts, drag/undo/reset, always-visible editor; fakes/offscreen only.");
    }
}
