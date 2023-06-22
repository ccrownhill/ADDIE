﻿module DiagramMainView
open Fulma

open Fable.React
open Fable.React.Props

open DiagramStyle
open ModelType
open FileMenuView
open Sheet.SheetInterface
open DrawModelType
open CommonTypes
open CanvasStateAnalyser
open Simulation
open SimulationHelpers

open Fable.Core
open Fable.Core.JsInterop
open Browser.Dom
open System



//------------------Buttons overlaid on Draw2D Diagram----------------------------------//
//--------------------------------------------------------------------------------------//

let viewOnDiagramButtons model dispatch =
    let sheetDispatch sMsg = dispatch (Sheet sMsg)
    let dispatch = SheetT.KeyPress >> sheetDispatch

    div [ canvasSmallMenuStyle ] [
        let canvasBut func label = 
            Button.button [ 
                Button.Props [ canvasSmallButtonStyle; OnClick func ] 
                Button.Modifiers [
                    //Modifier.TextWeight TextWeight.Bold
                    Modifier.TextColor IsLight
                    Modifier.BackgroundColor IsSuccess
                    ]
                ] 
                [ str label ]
        canvasBut (fun _ -> dispatch SheetT.KeyboardMsg.CtrlZ ) "< undo" 
        canvasBut (fun _ -> dispatch SheetT.KeyboardMsg.CtrlY ) "redo >" 
        canvasBut (fun _ -> dispatch SheetT.KeyboardMsg.CtrlC ) "copy" 
        canvasBut (fun _ -> dispatch SheetT.KeyboardMsg.CtrlV ) "paste" 

    ]

// -- Init Model

/// Initial value of model
let init() = {
    SpinnerPayload = None
    Spinner = None
    UserData = {
        WireType = BusWireT.Radial
        UserAppDir = None
        LastUsedDirectory = None
        RecentProjects = None
        Theme = SymbolT.ThemeType.Colourful
        }
    LastChangeCheckTime = 0.
    // Diagram = new Draw2dWrapper()
    Sheet = fst (SheetUpdate.init())
    IsLoading = false
    LastDetailedSavedState = ([],[])
    LastSimulatedCanvasState = None
    LastSelectedIds = [],[]
    CurrentSelected = [],[]
    SelectedComponent = None
    LastUsedDialogWidth = 1
    RightPaneTabVisible = Simulation
    SimSubTabVisible = DCsim
    CurrentProj = None
    Hilighted = ([], []), []
    Clipboard = [], []
    LastCreatedComponent = None
    SavedSheetIsOutOfDate = false
    PopupViewFunc = None
    PopupDialogData = {
        ProjectPath = ""
        Text = None
        Text2 = None
        Text3 = None
        Int = None
        Int2 = None
        VoltageSource = None
        BadLabel = false
        VSType= None
    }
    Notifications = {
        FromDiagram = None
        FromSimulation = None
        FromWaveSim = None
        FromFiles = None
        FromMemoryEditor = None
        FromProperties = None
    }
    TopMenuOpenState = Closed
    DividerDragMode = DragModeOff
    Pending = []
    UIState = None
    showGraphArea = false
    SimulationData = {
        ACOutput=None
        ACSource=None
        ACMagInDB=true
        ACFreqInHz=true
        TimeInput=None
        TimeOutput=None
        TheveninComp=None
    }
    PrevCanvasStateSizes = (0,0)
    PreviousDiodeModes = []
    Tests = []
    TheveninParams = None
}


let findInputNodeFromComp (nodeLst: (Component*int option) list list) compId =
    nodeLst |> List.findIndex (List.exists (fun (c,i)->c.Id = compId && i = (Some 0)))

let runSimulation (model:Model) dispatch = 
    let CS = model.Sheet.GetCanvasState ()
    match CS with
    |[],[] -> 
        dispatch ForceStopSim
        dispatch <| SetGraphVisibility false
    |_ ->
        let canvasState = CS |> combineGrounds
        let compsNo,connsNo= List.length (fst canvasState),List.length (snd canvasState)
        match model.PrevCanvasStateSizes = (compsNo,connsNo) with
        |true ->
            match model.Sheet.UpdateSim && model.Sheet.SimulationRunning with
            |false -> ()
            |true -> 
                match checkCanvasStateForErrors canvasState (model.SimSubTabVisible=TimeSim) with
                    |[] ->
                        ClosePropertiesNotification |> dispatch
                        CircuitHasNoErrors |> dispatch
                        SimulationUpdated |> dispatch
                        let res,componentCurrents,nodeLst,dm = Simulation.modifiedNodalAnalysisDC canvasState model.PreviousDiodeModes
                        //let equations = getDCEquations model.Sheet.DCSim (fst CS)
                        UpdateVoltages (Array.toList res) |> dispatch
                        UpdateCurrents componentCurrents |> dispatch
                        UpdateDiodeModes dm |> dispatch
                        UpdateDCSim {MNA=res;ComponentCurrents=componentCurrents;NodeList=nodeLst;Equations=[]} |> dispatch
                        
                        match res with
                        |[||] -> 
                            dispatch ForceStopSim
                            dispatch CircuitHasErrors
                            dispatch (SetPropertiesNotification (Notifications.errorPropsNotification "Unknown error in Circuit. Cannot run Simulation")) 
                        |_ ->
                            match model.SimSubTabVisible with
                            |DCsim -> ()
                            |ACsim -> 
                                dispatch UpdateNodes
                                dispatch <| ShowNodesOrVoltagesExplicitState Nodes
                                if model.showGraphArea then
                                    let outputNode = model.SimulationData.ACOutput |> Option.defaultValue "1" |> int
                                    let inputSource = model.SimulationData.ACSource |> Option.defaultValue ""
                                    let res = (frequencyResponse canvasState inputSource outputNode)
                                    UpdateACSim res |> dispatch
                                else ()
                            |TimeSim ->
                                dispatch UpdateNodes
                                dispatch <| ShowNodesOrVoltagesExplicitState Nodes
                                if model.showGraphArea then
                                    let inputSource = model.SimulationData.TimeInput |> Option.defaultValue "VS1" 
                                    let inputNode = inputSource |> findInputNodeFromComp nodeLst
                                    let outputNode = model.SimulationData.TimeOutput |> Option.defaultValue "1" |> int
                                    let timeSim = (transientAnalysis canvasState inputSource inputNode outputNode)
                                    UpdateTimeSim timeSim |> dispatch
                                else ()
                    |err ->
                        dispatch ForceStopSim
                        dispatch CircuitHasErrors
                        dispatch (SetPropertiesNotification (Notifications.errorPropsNotification err[0].Msg))
        |false ->
            dispatch <| UpdateCanvasStateSizes (compsNo,connsNo)
            dispatch ForceStopSim
            //dispatch <| CircuitHasErrors
            dispatch <| SetGraphVisibility false



let makeSelectionChangeMsg (model:Model) (dispatch: Msg -> Unit) (ev: 'a) =
    dispatch SelectionHasChanged

// -- Create View

let startStopSimDiv model dispatch = 
    let startStopState,startStopStr,startStopMsg = if model.Sheet.SimulationRunning then IsDanger,"Stop",ForceStopSim else if model.Sheet.CanRunSimulation then IsPrimary,"Start",SafeStartSim else IsWarning,"Restart"+CommonTypes.restartSymbol,SafeStartSim
    div [] [
        Heading.h4 [] [str "Start/Stop Simulation"]
        Button.button [
            Button.Color startStopState
            Button.OnClick (fun _-> 
                dispatch startStopMsg
                dispatch <| SetGraphVisibility false
                dispatch <| UpdateNodes
                
                )
        ] [str startStopStr]
        br []
        br []
    ]        

let createACSimSubTab model comps dispatch =
    let sourceOptions =
        comps
        |> List.collect (fun c->
            match c.Type with
            |VoltageSource _ -> [option [Value (c.Id)] [str (c.Label)]]
            |_ -> []
        )

    let outputOptions =
        [1..((List.length model.Sheet.DCSim.NodeList)-1)]
        |> List.collect (fun i ->
            [option [Value (string i)] [str ("Node "+(string i))]]    
        )

    let isDisabled = 
        if model.Sheet.CanRunSimulation && model.Sheet.SimulationRunning then
            match model.SimulationData.ACSource,model.SimulationData.ACOutput with
            |Some x,Some y when x<>"sel" && y<>"sel" -> false
            |_ -> true
        else true

    let magButtonText = if model.SimulationData.ACMagInDB then "dB" else "Normal"
    let freqButtonText = if model.SimulationData.ACFreqInHz then "Hz" else "rads/s"

    div [Style [Margin "20px"]] 
        [ 
            startStopSimDiv model dispatch

            div [Hidden (not model.Sheet.SimulationRunning)] [
            Heading.h5 [] [str "Setup AC Simulation"]
            div [Style [Width "50%"; Float FloatOptions.Left]] [
                Label.label [] [ str "Input" ]
                Label.label [ ]
                    [Select.select []
                    [ select [(OnChange(fun option -> SetSimulationACSource (Some option.Value) |> dispatch))]
                        ([option [Value ("sel")] [str ("Select")]] @ sourceOptions)
                        ]
                    ]
            
                Label.label [] [ str "Output" ]
                Label.label [ ]
                    [Select.select []
                    [ select 
                        [(OnChange(fun option -> SetSimulationACOut (Some option.Value) |> dispatch))]
                        ([option [Value ("sel")] [str ("Select")]] @ outputOptions)
                        ]
                    ]
                ]
            div [Style [Width "50%"; Float FloatOptions.Right]] [
                Label.label [] [ str "Magnitude" ]
                Label.label [ ]
                    [Button.button [Button.OnClick (fun _ -> dispatch SetSimulationACInDB); Button.Color IsLight] [str magButtonText]]
            
                Label.label [] [ str "Frequency" ]
                Label.label [ ]
                    [
                        Button.button [Button.OnClick (fun _ -> dispatch SetSimulationACInHz);Button.Color IsLight] [str freqButtonText] 
                    ]
                ]

            div [Style [Color "White"]] [str "f"]
            br []

            Button.button 
                [   Button.OnClick (fun _ -> 
                        RunSim |> dispatch
                        SetGraphVisibility true |> dispatch); 
                        Button.Color IsPrimary;
                        Button.Disabled isDisabled] 
                [str "Show"]

            ]
            br []
            div [] [str "AC Simulation explores how the circuit behaves over a wide range of frequencies. The plots produced by the AC Simulation represent the magnitude and the phase of the ratio (output_node/input_source). During simulation, all other sources are set to 0 and diodes are assumed to be in conducting mode."]
            
        ]

let createTimeSimSubTab model comps dispatch =
    let sourceOptions =
        comps
        |> List.collect (fun c->
            match c.Type with
            |VoltageSource _ -> [option [Value (c.Id)] [str (c.Label)]]
            |_ -> []
        )

    let outputOptions =
        [1..((List.length model.Sheet.DCSim.NodeList)-1)]
        |> List.collect (fun i ->
            [option [Value (string i)] [str ("Node "+(string i))]]    
        )

    let isDisabled = 
        match model.SimulationData.TimeInput,model.SimulationData.TimeOutput with
        |Some x,Some y when x<>"sel" && y<>"sel" -> 
            match checkTimeSimConditions comps with
            |[] -> false
            |err ->
                true
        |_ -> true

    
    div [Style [Margin "20px"]] 
        [ 
            startStopSimDiv model dispatch
            div [Hidden (not model.Sheet.SimulationRunning)] [
                Heading.h4 [] [str "Setup Time Simulation"]
                Label.label [] [ str "Input" ]
                Label.label [ ]
                    [Select.select []
                    [ select [(OnChange(fun option -> SetSimulationTimeSource (Some option.Value) |> dispatch))]
                        ([option [Value ("sel")] [str ("Select")]] @ sourceOptions)
                        ]
                    ]
            
                Label.label [] [ str "Output" ]
                Label.label [ ]
                    [Select.select [] [ select [(OnChange(fun option -> SetSimulationTimeOut (Some option.Value) |> dispatch))]
                        ([option [Value ("sel")] [str ("Select")]] @ outputOptions)
                        ]
                    ]
                br []
                br []
                Button.button 
                    [   Button.OnClick (fun _ -> 
                            RunSim |> dispatch
                            SetGraphVisibility true |> dispatch); 
                            Button.Color IsPrimary;
                            Button.Disabled isDisabled] 
                    [str "Show"]
            ]
            br []
            div [] [str "Time Simulation explores how the circuit behaves over time using 200 timesteps. The plots produced represent the input and output voltages, along with the two signals (steady-state and transient) that form the output voltage. Time simulation currently supports a maximum of one Voltage Source and one Capacitor or Inductor."]
            

        ]



let viewSimSubTab canvasState model dispatch =
    let comps',conns' = combineGrounds canvasState
    //match checkCanvasStateForErrors (comps',conns') with
    //|[] ->
    match model.SimSubTabVisible with
    | DCsim -> 
        let nodesVoltagesState = match model.Sheet.ShowNodesOrVoltages with |Neither -> IsDanger |Nodes -> IsWarning |Voltages -> IsPrimary
        let currentsState = if model.Sheet.ShowCurrents then IsPrimary else IsDanger
        div [Style [Margin "20px"]] 
            [ 
                let startStopState,startStopStr,startStopMsg = if model.Sheet.SimulationRunning then IsDanger,"Stop",ForceStopSim else if model.Sheet.CanRunSimulation then IsPrimary,"Start",SafeStartSim else IsWarning,"Restart"+CommonTypes.restartSymbol,SafeStartSim
                Heading.h4 [] [str "Start/Stop Simulation"]
                Button.button [
                Button.Color startStopState
                Button.OnClick (fun _-> 
                    dispatch startStopMsg
                    dispatch <| RunSim
                    dispatch <| SetGraphVisibility false
                        )
                ] [str startStopStr]
                br []
                br []
                div [Hidden <| not model.Sheet.SimulationRunning] [
                    Heading.h5 [] [str "Adjust on-Canvas Elements"]
                    Button.button [Button.OnClick(fun _ -> ShowOrHideCurrents |> dispatch); Button.Color currentsState] [ str "Currents" ]
                    span [Style [Width "20px";Color "White"]] [str "asd"]
                    Button.button [ 
                    Button.OnClick(fun _ -> 
                    UpdateNodes |> dispatch
                    ShowNodesOrVoltages |> dispatch); Button.Color nodesVoltagesState] [ str "Nodes/Voltages" ]
                                  
                    br []
                    br []
                    Heading.h5 [] [str "DC Results"]
                    div [] [

                    Menu.menu [Props [Class "py-1";]]  [
                        details [Open true;OnClick (fun _ -> dispatch RunSim)] [
                            summary [menuLabelStyle] [ str "Table Results" ]
                            Menu.list [] [getDCTable model.Sheet.DCSim (model.Sheet.SimulationRunning && model.Sheet.CanRunSimulation) canvasState ]
                        ]
                    ]
                
                    Menu.menu [Props [Class "py-1";]]  [
                        details [Open false;OnClick (fun _ -> dispatch RunSim)] [
                            summary [menuLabelStyle] [ str "Equations" ]
                            Menu.list [] [(getDCEquationsTable model.Sheet.DCSim.Equations)]
                        ]
                    ]

                    Menu.menu [Props [Class "py-1";]]  [
                        let paramsDiv =
                            match model.TheveninParams with
                            |None -> null
                            |Some par ->
                                let asstr = (string par.Resistance) + ", " + (string par.Voltage) + ", " + (string par.Current)
                                div [] [
                                    Table.table [] [
                                      tr [] [
                                        td [Style [Color "Black"; VerticalAlign "Middle"; WhiteSpace WhiteSpaceOptions.Pre]] [str "Rth"]
                                        td [Style [Color "Black"; VerticalAlign "Middle"; WhiteSpace WhiteSpaceOptions.Pre]] [str ((string (System.Math.Round (par.Resistance,6)))+" "+omegaString)]
                                      ]
                                      tr [] [
                                        td [Style [Color "Black"; VerticalAlign "Middle"; WhiteSpace WhiteSpaceOptions.Pre]] [str "Vth"]
                                        td [Style [Color "Black"; VerticalAlign "Middle"; WhiteSpace WhiteSpaceOptions.Pre]] [str ((string (System.Math.Round (par.Voltage,6)))+" V")]
                                      ]
                                      tr [] [
                                        td [Style [Color "Black"; VerticalAlign "Middle"; WhiteSpace WhiteSpaceOptions.Pre]] [str "Ino"]
                                        td [Style [Color "Black"; VerticalAlign "Middle"; WhiteSpace WhiteSpaceOptions.Pre]] [str ((string (System.Math.Round (par.Current,6)))+" A")]
                                      ]
                                    
                                    ]
                                    //str asstr
                                
                                ] 

                                
                        details [Open false;] [
                            summary [menuLabelStyle] [ str "Thevenin/Norton" ]
                            Menu.list [] [
                                str "Select a component from the dropdown below to view the thevenin or norton parameters representing the equivalent circuit seen by the component"
                                br []
                                Select.select []
                                    [ 
                                    let compOptions = 
                                        comps' 
                                        |> List.filter (fun c->match c.Type with |Opamp |Ground -> false |_ -> true)    
                                        |> List.collect (fun c->[option [Value (c.Id)] [str (c.Label)]])

                                    select 
                                        [(OnChange(fun option -> SetSimulationTheveninComp (Some option.Value) |> dispatch))]
                                        ([option [Value ("sel")] [str ("Select")]] @ compOptions)
                                        ]
                                Button.button [Button.Color IsPrimary;Button.OnClick(fun _ -> (dispatch SetSimulationTheveninParams))] [str "Find"]    
                                paramsDiv
                            ]
                        ]
                    ]
                    

                    //hack to avoid hidden results
                    div [Style [Height "100px"]] []
                ]]
            ]


    | ACsim -> 
        createACSimSubTab model comps' dispatch
    | TimeSim ->
        createTimeSimSubTab model comps' dispatch


/// Display the content of the right tab.
let private  viewRightTab canvasState model dispatch =
    let pane = model.RightPaneTabVisible
    match pane with
    | Catalogue ->
        
        div [ Style [Width "90%"; MarginLeft "5%"; MarginTop "15px" ; Height "calc(100%-100px)"] ] [
            Heading.h4 [] [ str "Catalogue" ]
            div [ Style [ MarginBottom "15px" ; Height "100%"; OverflowY OverflowOptions.Auto] ] 
                [ str "Click on a component to add it to the diagram. Hover on components for details." ]
            CatalogueView.viewCatalogue model dispatch
        ]
    | Properties ->
        div [ Style [Width "90%"; MarginLeft "5%"; MarginTop "15px" ] ] [
            Heading.h4 [] [ str "Component properties" ]
            SelectedComponentView.viewSelectedComponent model dispatch
        ]

    | Simulation ->
        let subtabs = 
            Tabs.tabs [ Tabs.IsFullWidth; Tabs.IsBoxed; Tabs.CustomClass "rightSectionTabs";
                        Tabs.Props [Style [Margin 0] ] ]  
                    [                 
                    Tabs.tab // step simulation subtab
                        [ Tabs.Tab.IsActive (model.SimSubTabVisible = DCsim) ]
                        [ a [  OnClick (fun _ -> dispatch <| ChangeSimSubTab DCsim) ] [str "DC Analysis"] ]  

                    (Tabs.tab // truth table tab to display truth table for combinational logic
                    [ Tabs.Tab.IsActive (model.SimSubTabVisible = ACsim) ]
                    [ a [  OnClick (fun _ -> dispatch <| ChangeSimSubTab ACsim; dispatch <| ShowNodesOrVoltagesExplicitState Nodes; dispatch <|SetGraphVisibility false) ] [str "Frequency Response"] ])

                    (Tabs.tab // wavesim tab
                    [ Tabs.Tab.IsActive (model.SimSubTabVisible = TimeSim) ]
                    [ a [  OnClick (fun _ -> dispatch <| ChangeSimSubTab TimeSim; dispatch <| ShowNodesOrVoltagesExplicitState Nodes; dispatch <|SetGraphVisibility false) ] [str "Time Analysis"] ])
                    ]
        div [ HTMLAttr.Id "RightSelection"; Style [Height "100%"]] 
            [
                //br [] // Should there be a gap between tabs and subtabs for clarity?
                subtabs
                viewSimSubTab canvasState model dispatch
            ]
    | Tests ->
        let temp = [true;true;true;true;true;true;true;true;true;true;]
        let tdTests = temp |> List.mapi (fun i b -> tr [] [td [] [str (sprintf "Test %i" (i+1))]; td [] [str (if b then "Pass" else "Fail")]])
        div [ Style [Width "90%"; MarginLeft "5%"; MarginTop "15px" ]] 
            [
                Heading.h4 [] [str "Tests"]
                Button.button [Button.Color IsPrimary; Button.OnClick(fun _->dispatch RunTests)] [str "Run Tests"]
                br []
                br []
                Table.table [] tdTests
                
            ]
    
/// determine whether moving the mouse drags the bar or not
let inline setDragMode (modeIsOn:bool) (model:Model) dispatch =
    fun (ev: Browser.Types.MouseEvent) ->        
        makeSelectionChangeMsg model dispatch ev
        //printfn "START X=%d, buttons=%d, mode=%A, width=%A, " (int ev.clientX) (int ev.buttons) model.DragMode model.ViewerWidth
        match modeIsOn, model.DividerDragMode with
        | true, DragModeOff ->  
            dispatch <| SetDragMode (DragModeOn (int ev.clientX))
        | false, DragModeOn _ -> 
            dispatch <| SetDragMode DragModeOff
        | _ -> ()

/// Draggable vertivcal bar used to divide Wavesim window from Diagram window
let dividerbar (model:Model) dispatch =
    let isDraggable = 
        model.RightPaneTabVisible = Simulation 
        && (model.SimSubTabVisible = TimeSim 
        || model.SimSubTabVisible = ACsim)
    let heightAttr = 
        let rightSection = document.getElementById "RightSection"
        if (isNull rightSection) then Height "100%"
        else Height "100%" //rightSection.scrollHeight
    let variableStyle = 
        if isDraggable then [
            BackgroundColor "grey"
            Cursor "ew-resize" 
            Width Constants.dividerBarWidth

        ] else [
            BackgroundColor "lightgray"
            Width "2px"
            Height "100%"

        ]
    let commonStyle = [
            heightAttr
            Float FloatOptions.Left
        ]
    div [
            Style <| commonStyle @ variableStyle
            OnMouseDown (setDragMode true model dispatch)       
        ] []

let viewRightTabs canvasState model dispatch =
    /// Hack to avoid scrollbar artifact changing from Simulation to Catalog
    /// The problem is that the HTML is bistable - with Y scrollbar on the catalog <aside> 
    /// moves below the tab body div due to reduced available width, keeping scrollbar on. 
    /// Not fully understood.
    /// This code temporarily switches the scrollbar off during the transition.
    let scrollType = 
            OverflowOptions.Auto

    let testTab = 
        if JSHelpers.debugLevel <> 0 then
            Tabs.tab // simulation tab to run tests
                [ Tabs.Tab.IsActive (model.RightPaneTabVisible = Tests)]
                [ a [  OnClick (fun _ ->    
                    dispatch <| ChangeRightTab Tests) ] [str "Tests"] ]
        else
            null
            
    
    div [HTMLAttr.Id "RightSelection";Style [ Height "100%"; OverflowY OverflowOptions.Auto]] [
        Tabs.tabs [ 
            Tabs.IsFullWidth; 
            Tabs.IsBoxed; 
            Tabs.CustomClass "rightSectionTabs"
            Tabs.Props [Style [Margin 0]] ; 
            
        ] [
            Tabs.tab // catalogue tab to add components
                [ Tabs.Tab.IsActive (model.RightPaneTabVisible = Catalogue) ]
                [ a [ OnClick (fun _ -> 
                        let target = 
                            if model.RightPaneTabVisible = Simulation then
                                Catalogue else
                                Catalogue
                        dispatch <| ChangeRightTab target ) ] [str "Catalogue" ] ]
            Tabs.tab // Properties tab to view/change component properties
                [ Tabs.Tab.IsActive (model.RightPaneTabVisible = Properties) ]                                   
                [ a [ OnClick (fun _ -> dispatch <| ChangeRightTab Properties )] [str "Properties"  ] ]
            Tabs.tab // simulation tab to view all simulators
                [ Tabs.Tab.IsActive (model.RightPaneTabVisible = Simulation) ]
                [ a [  OnClick (fun _ -> 
                    dispatch <| ChangeRightTab Simulation ) ] [str "Simulations"] ]
            testTab
        ]
        div [HTMLAttr.Id "TabBody"; belowHeaderStyle "36px"; Style [OverflowY scrollType]] [viewRightTab canvasState model dispatch]

    ]
let mutable testState:CanvasState = [],[]
let mutable lastDragModeOn = false

//---------------------------------------------------------------------------------------------------------//
//------------------------------------------VIEW FUNCTION--------------------------------------------------//
//---------------------------------------------------------------------------------------------------------//
/// Top-level application view: as react components that create a react virtual-DOM
let displayView model dispatch =
    JSHelpers.traceIf "view" (fun _ -> "View Function...")
    let windowX,windowY =
        int Browser.Dom.self.innerWidth, int Browser.Dom.self.innerHeight
    //let selectedComps, selectedconns = 
    //    model.Diagram.GetSelected()
    //    |> Option.map extractState
    //    |> Option.defaultValue ([],[])
    
    // TODO
//    let sd = scrollData model
//    let x' = sd.SheetLeft+sd.SheetX
//    let y' = sd.SheetTop+sd.SheetY


    let inline processAppClick topMenu dispatch (ev: Browser.Types.MouseEvent) =
        if topMenu <> Closed then 
            dispatch <| Msg.SetTopMenu Closed
    /// used only to make the divider bar draggable
    let inline processMouseMove (keyUp: bool) (ev: Browser.Types.MouseEvent) =
        //printfn "X=%d, buttons=%d, mode=%A, width=%A, " (int ev.clientX) (int ev.buttons) model.DragMode model.ViewerWidth
        if ev.buttons = 1. then 
            dispatch SelectionHasChanged
        //match model.DividerDragMode, ev.buttons, keyUp with
        //| DragModeOn pos , 1., false-> 
        //    let newWidth = model.WaveSimViewerWidth - int ev.clientX + pos
        //    let w = 
        //        newWidth
        //        |> max minViewerWidth
        //        |> min (windowX - minEditorWidth())
        //    dispatch <| SetDragMode (DragModeOn (int ev.clientX - w + newWidth))
        //    dispatch <| SetViewerWidth w 
        //| DragModeOn pos, _, true ->
        //    let newWidth = model.WaveSimViewerWidth - int ev.clientX + pos
        //    let w =
        //        newWidth
        //        |> max minViewerWidth
        //        |> min (windowX - minEditorWidth())
        //    setViewerWidthInWaveSim w model dispatch
        //    dispatch <| SetDragMode DragModeOff
        //    dispatch <| SetViewerWidth w 
        //| _ -> 
        ()

    let headerHeight = getHeaderHeight
    let sheetDispatch sMsg = dispatch (Sheet sMsg)

    // the whole app window
    let cursorText = model.Sheet.CursorType.Text()
    let topCursorText = match model.Sheet.CursorType with | SheetT.Spinner -> "wait" | _ -> ""

    let conns = BusWire.extractConnections model.Sheet.Wire
    let comps = SymbolUpdate.extractComponents model.Sheet.Wire.Symbol
    let canvasState = comps,conns  
    
    // run Simulation
    runSimulation model dispatch

    match model.Spinner with
    | Some fn -> 
        dispatch <| UpdateModel fn
    | None -> ()
    div [ HTMLAttr.Id "WholeApp"
          Key cursorText
          OnMouseMove (processMouseMove false)
          OnClick (processAppClick model.TopMenuOpenState dispatch)
          OnMouseUp (processMouseMove true)
          Style [ 
            //CSSProp.Cursor cursorText
            UserSelect UserSelectOptions.None
            BorderTop "2px solid lightgray"
            BorderBottom "2px solid lightgray"
            OverflowX OverflowOptions.Auto
            Height "calc(100%-4px)"
            Cursor topCursorText ] ] [
                

        FileMenuView.viewNoProjectMenu model dispatch
        
        PopupView.viewPopup model dispatch 
        // Top bar with buttons and menus: some subfunctions are fed in here as parameters because the
        // main top bar function is early in compile order
        FileMenuView.viewTopMenu model dispatch

        //if model.PopupDialogData.Progress = None then
        Sheet.view model.Sheet headerHeight (canvasVisibleStyleList model) sheetDispatch
        
        // transient pop-ups
        Notifications.viewNotifications model dispatch
        // editing buttons overlaid bottom-left on canvas
        viewOnDiagramButtons model dispatch

        //--------------------------------------------------------------------------------------//
        //------------------------ left section for Sheet (NOT USED) ---------------------------//
        // div [ leftSectionStyle model ] [ div [ Style [ Height "100%" ] ] [ Sheet.view model.Sheet sheetDispatch ] ]

        //--------------------------------------------------------------------------------------//
        //---------------------------------right section----------------------------------------//
        // right section has horizontal divider bar and tabs
        div [ HTMLAttr.Id "RightSection"; rightSectionStyle model ]
                // vertical and draggable divider bar
            [ 
                // dividerbar model dispatch
                // tabs for different functions
                viewRightTabs canvasState model dispatch ] 
         
        div [HTMLAttr.Id "BottomSection"; bottomSectionStyle model; Hidden (not model.showGraphArea)]
                (Graph.viewGraph model dispatch)  
            ]

