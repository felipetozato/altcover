﻿// Based upon C# code by Sergiy Sakharov (sakharov@gmail.com)
// http://code.google.com/p/dot-net-coverage/source/browse/trunk/Coverage.Counter/Coverage.Counter.csproj

namespace AltCover.Recorder

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Resources
open System.Runtime.CompilerServices

[<System.Runtime.InteropServices.ProgIdAttribute("ExcludeFromCodeCoverage hack for OpenCover issue 615")>]
type internal Close =
    | DomainUnload
    | ProcessExit
    | Pause
    | Resume

[<System.Runtime.InteropServices.ProgIdAttribute("ExcludeFromCodeCoverage hack for OpenCover issue 615")>]
type internal Carrier =
    | SequencePoint of String*int*Track

[<System.Runtime.InteropServices.ProgIdAttribute("ExcludeFromCodeCoverage hack for OpenCover issue 615")>]
type internal Message =
    | AsyncItem of Carrier
    | Item of Carrier*AsyncReplyChannel<unit>
    | Finish of Close * AsyncReplyChannel<unit>

module Instance =

  // Can't hard-code what with .net-core and .net-core tests as well as classic .net
  // all giving this a different namespace
  let private resource = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                         |> Seq.map (fun s -> s.Substring(0, s.Length - 10)) // trim ".resources"
                         |> Seq.find (fun n -> n.EndsWith("Strings", StringComparison.Ordinal))
  let internal resources = ResourceManager(resource , Assembly.GetExecutingAssembly())

  let GetResource s =
    [
      System.Globalization.CultureInfo.CurrentUICulture.Name
      System.Globalization.CultureInfo.CurrentUICulture.Parent.Name
      "en"
    ]
    |> Seq.map (fun l -> resources.GetString(s + "." + l))
    |> Seq.tryFind (String.IsNullOrEmpty >> not)

  /// <summary>
  /// Gets the location of coverage xml file
  /// This property's IL code is modified to store actual file location
  /// </summary>
  [<MethodImplAttribute(MethodImplOptions.NoInlining)>]
  let ReportFile = "Coverage.Default.xml"

  /// <summary>
  /// Accumulation of visit records
  /// </summary>
  let internal Visits = new Dictionary<string, Dictionary<int, int * Track list>>();

  /// <summary>
  /// Gets the unique token for this instance
  /// This property's IL code is modified to store a GUID-based token
  /// </summary>
  [<MethodImplAttribute(MethodImplOptions.NoInlining)>]
  let Token = "AltCover"

  /// <summary>
  /// Gets the style of the associated report
  /// This property's IL code is modified to store the user chosen override if applicable
  /// </summary>
  [<MethodImplAttribute(MethodImplOptions.NoInlining)>]
  let CoverageFormat = ReportFormat.NCover

  /// <summary>
  /// Gets the frequency of time sampling
  /// This property's IL code is modified to store the user chosen override if applicable
  /// </summary>
  [<MethodImplAttribute(MethodImplOptions.NoInlining)>]
  let Timer = 0L

  /// <summary>
  /// Gets or sets the current test method
  /// </summary>
  type private CallStack =
    [<ThreadStatic;DefaultValue>]
    static val mutable private instance:Option<CallStack>

    val mutable private caller:int list
    private new (x:int) = {caller = [x]}

    static member Instance =
        match CallStack.instance with
        | None -> CallStack.instance <- Some (CallStack(0))
        | _ -> ()

        CallStack.instance.Value

    member self.Push x =  self.caller <- x :: self.caller
                          //let s = sprintf "push %d -> %A" x self.caller
                          //System.Diagnostics.Debug.WriteLine(s)

    member self.Pop () = self.caller <- match self.caller with
                                         | []
                                         | [0] -> [0]
                                         | _::xs -> xs
                         //let s = sprintf "pop -> %A"self.caller
                         //System.Diagnostics.Debug.WriteLine(s)

    member self.CallerId () = Seq.head self.caller
                              (*let x = Seq.head self.caller
                              let s = sprintf "peek %d" x
                              System.Diagnostics.Debug.WriteLine(s)
                              x*)

  let Push x = CallStack.Instance.Push x
  let Pop () = CallStack.Instance.Pop ()
  let CallerId () = CallStack.Instance.CallerId ()

  /// <summary>
  /// Serialize access to the report file across AppDomains for the classic mode
  /// </summary>
  let internal mutex = new System.Threading.Mutex(false, Token + ".mutex");

  let SignalFile () = ReportFile + ".acv"

  /// <summary>
  /// Reporting back to the mother-ship
  /// </summary>
  let mutable internal trace = Tracer.Create (SignalFile ())

  let internal WithMutex (f : bool -> 'a) =
    let own = mutex.WaitOne(1000)
    try
      f(own)
    finally
      if own then mutex.ReleaseMutex()

  let InitialiseTrace () =
    WithMutex (fun _ -> let t = Tracer.Create (SignalFile ())
                        trace <- t.OnStart ())

  let internal Watcher = new FileSystemWatcher()
  let mutable internal Recording = true

  let internal FlushAll () =
    trace.OnConnected (fun () -> trace.OnFinish Visits)
      (fun () ->
      match Visits.Count with
      | 0 -> ()
      | _ -> let counts = Dictionary<string, Dictionary<int, int * Track list>> Visits
             Visits.Clear()
             WithMutex (fun own ->
                let delta = Counter.DoFlush ignore (fun _ _ -> ()) own counts CoverageFormat ReportFile None
                GetResource "Coverage statistics flushing took {0:N} seconds"
                |> Option.iter (fun s -> Console.Out.WriteLine(s, delta.TotalSeconds))
             ))

  /// <summary>
  /// This method flushes hit count buffers.
  /// </summary>
  let internal FlushCounterImpl action mode =
    (mode.ToString() + "Handler")
    |> GetResource
    |> Option.iter Console.Out.WriteLine
    match mode with
    | Resume ->
      Visits.Clear()
      InitialiseTrace ()
    | Pause -> action()
               InitialiseTrace ()
    | _ -> action()

  let internal FlushCounterDefault mode =
     FlushCounterImpl FlushAll mode

  let internal TraceVisit moduleId hitPointId context =
     trace.OnVisit Visits moduleId hitPointId context

  /// <summary>
  /// This method is executed from instrumented assemblies.
  /// </summary>
  /// <param name="moduleId">Assembly being visited</param>
  /// <param name="hitPointId">Sequence Point identifier</param>
  let internal VisitImpl moduleId hitPointId context =
    if not <| String.IsNullOrEmpty(moduleId) then
      trace.OnConnected (fun () -> TraceVisit moduleId hitPointId context)
                        (fun () -> Counter.AddVisit Visits moduleId hitPointId context)

  let rec private loop (inbox:MailboxProcessor<Message>) =
          async {
             // release the wait every half second
             let! opt = inbox.TryReceive(500)
             match opt with
             | None -> return! loop inbox
             | Some msg ->
                 match msg with
                 | AsyncItem (SequencePoint (moduleId, hitPointId, context)) ->
                     VisitImpl moduleId hitPointId context
                     return! loop inbox
                 | Item (SequencePoint (moduleId, hitPointId, context), channel) ->
                     VisitImpl moduleId hitPointId context
                     channel.Reply ()
                     return! loop inbox
                 | Finish (mode, channel) ->
                     FlushCounterDefault mode
                     channel.Reply ()
                     if mode = Pause || mode = Resume then return! loop inbox
          }

  let internal MakeMailbox () =
    new MailboxProcessor<Message>(loop)

  let mutable internal mailbox = MakeMailbox ()

  let internal Backlog () =
    mailbox.CurrentQueueLength

  let private IsOpenCoverRunner() =
     (CoverageFormat = ReportFormat.OpenCoverWithTracking) &&
       ((trace.Definitive && trace.Runner) ||
        (ReportFile <> "Coverage.Default.xml" && System.IO.File.Exists (ReportFile + ".acv")))

  let internal Granularity() = Timer

  let internal Clock () = DateTime.UtcNow.Ticks

  let internal PayloadSelection clock frequency wantPayload =
    if wantPayload () then
       match (frequency(), CallerId()) with
       | (0L, 0) -> Null
       | (t, 0) -> Time (t*(clock()/t))
       | (0L, n) -> Call n
       | (t, n) -> Both (t*(clock()/t), n)
    else Null

  let internal PayloadControl = PayloadSelection Clock

  let internal PayloadSelector enable =
    PayloadControl Granularity enable

  let mutable internal Wait = 10

  let internal VisitSelection (f: unit -> bool) track moduleId hitPointId =
    // When writing to file for the runner to process,
    // make this semi-synchronous to avoid choking the mailbox
    // Backlogs of over 90,000 items were observed in self-test
    // which failed to drain during the ProcessExit grace period
    // when sending only async messages.
    let message = SequencePoint (moduleId, hitPointId, track)
    if f() then
       mailbox.TryPostAndReply ((fun c -> Item (message, c)), Wait) |> ignore
    else message |> AsyncItem |> mailbox.Post

  let Visit moduleId hitPointId =
    if Recording then
      VisitSelection (fun () -> trace.IsConnected() || Backlog() > 10)
                     (PayloadSelector IsOpenCoverRunner) moduleId hitPointId

  let mutable internal TerminalSent = false

  let internal FlushCounter (finish:Close) _ =
    lock mailbox (fun () ->
      finish.ToString()
      |> GetResource
      |> Option.iter Console.Out.WriteLine
      Recording <- finish = Resume
      let isTerminal = (finish = Pause || finish = Resume) |> not
      if TerminalSent |> not then mailbox.TryPostAndReply ((fun c -> Finish (finish, c)),
                                                           if isTerminal then 2000 else -1)
                                  |> ignore
      TerminalSent <- isTerminal)

  let internal RunMailbox () =
    mailbox <- MakeMailbox ()
    TerminalSent <- false
    mailbox.Start()

  // Register event handling
  let internal StartWatcher() =
     Watcher.Path <- Path.GetDirectoryName <| SignalFile()
     Watcher.Filter <- Path.GetFileName <| SignalFile()
     Watcher.Created.Add (FlushCounter Resume)
     Watcher.Deleted.Add (FlushCounter Pause)
     Watcher.EnableRaisingEvents <- Watcher.Path |> String.IsNullOrEmpty |> not

  do
    AppDomain.CurrentDomain.DomainUnload.Add(FlushCounter DomainUnload)
    AppDomain.CurrentDomain.ProcessExit.Add(FlushCounter ProcessExit)
    StartWatcher ()
    InitialiseTrace ()
    RunMailbox ()