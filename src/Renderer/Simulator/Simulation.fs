﻿module Simulation

open CommonTypes
open MathJsHelpers
open JSHelpers
open System
open CanvasStateAnalyser


//////////////////  SIMULATION HELPERS   /////////////////

let combineGrounds (comps,conns) =
    let allGrounds = comps |> List.filter (fun c->c.Type = Ground)
    match List.isEmpty allGrounds with
    |true -> failwithf "There is no ground in the circuit"
    |false -> 
        let mainGround,mainPort = allGrounds[0], allGrounds[0].IOPorts[0]
        let otherGrounds = List.removeAt 0 allGrounds
        let otherPortsIds = otherGrounds |> List.map (fun c -> c.IOPorts[0].Id)

        let conns' =
            conns
            |> List.map (fun conn->
                if List.exists (fun i -> i=conn.Source.Id) otherPortsIds then
                    {conn with Source = mainPort}
                elif List.exists (fun i -> i=conn.Target.Id) otherPortsIds then
                    {conn with Target = mainPort}
                else
                    conn        
            )

        let comps' = comps |> List.filter (fun c-> not (List.exists (fun (ground:Component) -> ground.Id = c.Id) otherGrounds))

        (comps',conns')

/// Transforms locally the canvas state to replace all
/// Inductors by 0-Volts DC Sources (short circuit) for
/// the MNA to work 
let shortInductorsForDC (comps,conns) =
    let comps' =
        comps
        |> List.map (fun c->
            match c.Type with
            |Inductor _ -> {c with Type = VoltageSource (DC 0.)}
            |_ -> c
        )
    (comps',conns)

   
let findInputAtTime vs t =
        match vs with
        |None -> failwithf "No Voltage Source present in the circuit"
        |Some v ->
            match v.Type with
            |VoltageSource (DC x) -> x
            |VoltageSource (Sine (a,o,f)) ->
                a*sin(2.*System.Math.PI*f*t+o)
            |VoltageSource (Pulse (v1,v2,f))->
                let md = t % f
                if md < f/2. then v1
                else v2
            |_ -> failwithf "Impossible"




/// Calculates the elements of the vector B of MNA
/// which is of the form:
/// [I1,I2,...,In,Va,Vb,...,Vm,0o] 
/// n -> number of nodes, m number of Voltage Sources, o number of opamps
let calcVectorBElement row (nodeToCompsList:(Component*int option) list list) =
    let nodesNo = List.length nodeToCompsList
    
    let findNodeTotalCurrent currentNode = 
        ({Re=0.;Im=0.}, currentNode) ||> List.fold (fun s (comp,no) ->
            //printfn "ROW= %i, comp= %A, no= %A" row comp no
            match (comp.Type,no) with
            |CurrentSource (v,_),Some 0 -> s+{Re=v;Im=0.}
            |CurrentSource (v,_), Some 1 -> s-{Re=v;Im=0.}
            |_ -> s
        )

    let allVoltageSources = 
        nodeToCompsList
        |> List.removeAt 0
        |> List.collect (fun nodeComps ->
            nodeComps
            |> List.collect (fun (comp,_) ->
                match comp.Type with
                |VoltageSource (_) -> [comp]
                |_ -> []
            )
        )
        |> List.distinctBy (fun c->c.Id)
    
    let vsNo = List.length allVoltageSources

    if row < nodesNo then   //current rows
        let currentNode = nodeToCompsList[row]
        findNodeTotalCurrent currentNode
    else if row < (nodesNo + vsNo) then  //voltage source rows
        allVoltageSources[row-nodesNo]
        |> (fun c -> 
            match c.Type with
            |VoltageSource (DC v) -> {Re=v;Im=0.}
            |VoltageSource (Sine (a,o,f)) -> {Re=1;Im=0.}
            |_ -> failwithf "Impossible"
        )
    else    //opamp rows
        {Re=0.;Im=0.}



/// Calculates the value of the MNA matrix at the specified
/// row and column (Count starts from 1)
/// Matrix consist of submatrices
/// More info on github
/// A = [G B]
///     [C D]
let calcMatrixElementValue row col (nodeToCompsList:(Component*int option) list list) omega vecB = 
    let findMatrixGCompValue (comp,no) omega =
        match comp.Type with
        |Resistor (v,_) -> {Re= (1./v); Im=0.}
        |Inductor (v,_) -> {Re = 0.; Im= -(1./(v*omega))}
        |Capacitor (v,_) -> {Re = 0.; Im= (v*omega)}
        |CurrentSource _ |VoltageSource _ |Opamp -> {Re = 0.0; Im=0.0}
        |_ -> failwithf "Not Implemented yet"
    

    let findMatrixBVoltageValue (comp,no) (vsAndOpamps:Component list) col nodesNo =
        match comp.Type with
        |VoltageSource (_) |Opamp ->
            match comp.Id = vsAndOpamps[col-nodesNo].Id with
            |true ->
                match (comp.Type,no) with
                |VoltageSource (_),Some 1 -> {Re = -1.0; Im=0.0}
                |VoltageSource (_), Some 0 -> {Re = 1.0; Im=0.0}
                |Opamp, Some 2 -> {Re = 1.0; Im=0.0}
                |Opamp, _ -> {Re = 0.0; Im=0.0}
                |_ -> failwithf "Unable to identify port of Voltage Source"
            |false -> {Re = 0.0; Im=0.0}
        |_ -> {Re = 0.0; Im=0.0}

    let findMatrixCVoltageValue (comp,no) (vsAndOpamps:Component list) col nodesNo =
        match comp.Type with
        |VoltageSource (_) |Opamp ->
            match comp.Id = vsAndOpamps[col-nodesNo].Id with
            |true ->
                match (comp.Type,no) with
                |VoltageSource (_),Some 1 -> {Re = -1.0; Im=0.0}
                |VoltageSource (_), Some 0 -> {Re = 1.0; Im=0.0}
                |Opamp, Some 0 -> {Re = 1.0; Im=0.0}
                |Opamp, Some 1 -> {Re = -1.0; Im=0.0}
                |Opamp, _ -> {Re = 0.0; Im=0.0}
                |_ -> failwithf "Unable to identify port of Voltage Source"
            |false -> {Re = 0.0; Im=0.0}
        |_ -> {Re = 0.0; Im=0.0}
                
    let nodesNo = List.length nodeToCompsList
    
    let allVoltageSources = 
        nodeToCompsList
        |> List.removeAt 0
        |> List.collect (fun nodeComps ->
            nodeComps
            |> List.collect (fun (comp,_) ->
                match comp.Type with
                |VoltageSource (_) -> [comp]
                |_ -> []
            )
        )
        |> List.distinctBy (fun c->c.Id)
    
    let allOpamps = 
        nodeToCompsList
        |> List.removeAt 0
        |> List.collect (fun nodeComps ->
            nodeComps
            |> List.collect (fun (comp,_) ->
                match comp.Type with
                |Opamp -> [comp]
                |_ -> []
            )
        )
        |> List.distinctBy (fun c->c.Id)

    let vsNo = List.length allVoltageSources

    let vsAndOpamps = allVoltageSources @ allOpamps


    if (row < nodesNo && col < nodesNo) then
    // conductance matrix
        if row=col then
            ({Re=0.;Im=0.}, nodeToCompsList[row]) 
            ||> List.fold(fun s c ->
                s + (findMatrixGCompValue c omega)
            )

        else
            ({Re=0.;Im=0.}, findComponentsBetweenNodes (row,col) nodeToCompsList)
            ||> List.fold(fun s c ->
                s - (findMatrixGCompValue c omega)
            )
    else 
    // extra elements for modified nodal analysis (vs / opamp)
        if (row >= nodesNo && col >= nodesNo) then
            // matrix D is always 0
            {Re=0.;Im=0.}
        else
            if row < nodesNo then
                ({Re=0.;Im=0.}, nodeToCompsList[row])
                ||> List.fold(fun s c ->
                    s + (findMatrixBVoltageValue c vsAndOpamps col nodesNo)
                )            
            else
                ({Re=0.;Im=0.}, nodeToCompsList[col])
                ||> List.fold(fun s c ->
                    s + (findMatrixCVoltageValue c vsAndOpamps row nodesNo)
                )       



/// Finds resistor, vs and opamp currents
/// Current direction in vs will always be from + to -
/// In opamps: output to input(V+/V-)
/// In resistors: left2right or top2bottom
let findComponentCurrents results nodesNo nodeLst conns =
    let allDCVoltageSources = 
        nodeLst
        |> List.removeAt 0
        |> List.collect (fun nodeComps ->
            nodeComps
            |> List.collect (fun (comp,_) ->
                match comp.Type with
                |VoltageSource (_) -> [comp]
                |_ -> []
            )
        )
        |> List.distinctBy (fun c->c.Id)
    
    let allOpamps = 
        nodeLst
        |> List.removeAt 0
        |> List.collect (fun nodeComps ->
            nodeComps
            |> List.collect (fun (comp,_) ->
                match comp.Type with
                |Opamp -> [comp]
                |_ -> []
            )
        )
        |> List.distinctBy (fun c->c.Id)

    let allResistors = 
        nodeLst
        |> List.removeAt 0
        |> List.collect (fun nodeComps ->
            nodeComps
            |> List.collect (fun (comp,_) ->
                match comp.Type with
                |Resistor _ -> [comp]
                |_ -> []
            )
        )
        |> List.distinctBy (fun c->c.Id)
    
    let vsAndOpampCurrents =
        allDCVoltageSources@allOpamps
        |> List.mapi (fun i comp ->
            let current = Array.tryItem (i+nodesNo) results
            match current with
            |Some curr -> 
                match comp.Type,comp.SymbolInfo with
                |Opamp,Some x when x.STransform.Rotation = Degree270||x.STransform.Rotation = Degree0 -> (ComponentId comp.Id, -curr)
                |VoltageSource _,Some x when x.STransform.Rotation = Degree270||x.STransform.Rotation = Degree0 -> (ComponentId comp.Id, -curr)
                |_-> (ComponentId comp.Id, curr)
            |None -> failwithf "Attempting to find current that doesn't exist in the result vector"
        )

    let topLeftToBottomRight n1 n2 (comp: Component) =
        let conns1 = findConnectionsOnNode nodeLst n1 conns
        let conns2 = findConnectionsOnNode nodeLst n2 conns
        let portId1 = conns1 |> List.collect (fun conn -> 
            if conn.Source.Id = comp.IOPorts[0].Id 
            || conn.Source.Id = comp.IOPorts[1].Id 
                then [conn.Source.Id]
                else if conn.Target.Id = comp.IOPorts[0].Id 
                || conn.Target.Id = comp.IOPorts[1].Id 
                then [conn.Target.Id]
                else []
            )
        let portId2 = conns2 |> List.collect (fun conn -> 
            if conn.Source.Id = comp.IOPorts[0].Id 
            || conn.Source.Id = comp.IOPorts[1].Id 
                then [conn.Source.Id]
                else if conn.Target.Id = comp.IOPorts[0].Id 
                || conn.Target.Id = comp.IOPorts[1].Id 
                then [conn.Target.Id]
                else []
            )

        let edge1,edge2 = 
            match comp.SymbolInfo with
            |None -> Top,Bottom
            |Some info ->
                info.PortOrientation[portId1[0]], info.PortOrientation[portId2[0]]

        match edge1,edge2 with
        |Top,Bottom |Left,Right -> n2,n1
        |Bottom,Top |Right,Left -> n1,n2
        |_ -> failwithf "Cannot identify node orientation relative to symbol"


    let resistorCurrents= 
        allResistors
        |> List.map (findNodesOfComp nodeLst)
        |> List.mapi (fun i (n1,n2)->
            let n1',n2' = topLeftToBottomRight n1 n2 allResistors[i]
            let v1 = if n1'=0 then 0. else results[n1'-1]
            let v2 = if n2'=0 then 0. else results[n2'-1]
            let resistance = 
                match allResistors[i].Type with 
                |Resistor (v,_) -> v 
                |_ -> failwithf "Attempting to find Resistance of a non-Resistor comp"
            (ComponentId allResistors[i].Id,(v2-v1)/resistance)
        )
        |> List.map (fun (r,v)->(r, System.Math.Round (v,4)))
        
    vsAndOpampCurrents
    |> List.append resistorCurrents
    |> Map.ofList


///////// MAIN SIMULATION FUNCTIONS //////////



/// Performs MNA on the current canvas state. 
/// Returns: (i) result of MNA (node Voltages, vs/opamp currents)
/// (ii) component Currents, (iii) nodeToCompsList
let modifiedNodalAnalysisDC (comps,conns) =
    
    // transform canvas state for DC Analysis  
    let comps',conns' = 
        (comps,conns)
        |> combineGrounds
        |> shortInductorsForDC 

    
    /////// required information ////////
    
    let nodeLst = createNodetoCompsList (comps',conns')
    
    let vs = comps' |> List.filter (fun c-> match c.Type with |VoltageSource _ -> true |_ -> false) |> List.length
    let opamps = comps' |> List.filter (fun c-> c.Type=Opamp) |> List.length

    let n = List.length nodeLst - 1 + vs + opamps
    
    
    let arr = Array.create n 0.0
    let matrix = Array.create n arr


    ////////// matrix creation ///////////
    

    let vecB =
        Array.create n {Re=0.0;Im=0.0}
        |> Array.mapi (fun i v -> (calcVectorBElement (i+1) nodeLst))
        |> Array.map (fun c -> c.Re)
    
    let flattenedMatrix =
        matrix
        |> Array.mapi (fun i top ->
            top
            |> Array.mapi (fun j v ->
                calcMatrixElementValue (i+1) (j+1) nodeLst 0. vecB
            )
        )
        |> Array.collect (id)
        
    //printfn "flattened matrix = %A" flattenedMatrix
    ////////////////////////////////

    ////////// solve ///////////

    let mul = safeSolveMatrixVec flattenedMatrix vecB

    let result = mul |> Array.map (fun x->System.Math.Round (x,4))
    
    let componentCurrents = findComponentCurrents result (List.length nodeLst-1) nodeLst conns'

    result, componentCurrents, nodeLst 



let acAnalysis matrix nodeLst vecB wmega outputNode =    
    let flattenedMatrix =
        matrix
        |> Array.mapi (fun i top ->
            top
            |> Array.mapi (fun j v ->
                calcMatrixElementValue (i+1) (j+1) nodeLst wmega vecB
            )
        )
        |> Array.collect (id)
    let result = safeSolveMatrixVecComplex flattenedMatrix vecB
    printfn "result %A" result
    result[outputNode-1]        

     

let frequencyResponse (comps,conns) outputNode  =

    // transform canvas state for DC Analysis  
    let comps',conns' = combineGrounds (comps,conns)

    let nodeLst = createNodetoCompsList (comps',conns')

    let vs = 
        comps'
        |> List.filter (fun c-> match c.Type with |VoltageSource _ -> true |_ -> false)
        |> List.length

    let opamps = comps' |> List.filter (fun c-> c.Type=Opamp) |> List.length

    let n = List.length nodeLst - 1 + vs + opamps
    let arr = Array.create n 0.0
    let matrix = Array.create n arr
          
    
    let vecB =
        Array.create n {Re=0.0;Im=0.0}
        |> Array.mapi (fun i v -> (calcVectorBElement (i+1) nodeLst))
    

    let frequencies = [0.0..0.05..7.0] |> List.map (fun x -> 10.**x)
    
    frequencies
    |> List.map (fun wmega->
        acAnalysis matrix nodeLst vecB wmega outputNode
    )
    |> List.map complexCToP
    



///////////////
let findCompType compType =
    (fun (x:Component) -> match x.Type with |a when a=compType -> true |_ -> false)

/// need to make sure that only 1 capacitor/inductor is present in the circuit
let replaceCLWithCS csValue (comps,conns) =
    let comps' =
        comps
        |> List.map (fun c->
            match c.Type with
            |Capacitor _ |Inductor _ -> {c with Type = CurrentSource (csValue,"0")}
            |_ -> c    
        )
    comps',conns
    

let replaceCLWithWire (comps,conns) =

    //does this work??? 
    let keepRL = List.tryFind (fun c-> match c.Type with |Capacitor _->true |_ ->false ) comps    
    match keepRL with
    |None -> (comps,conns)
    |Some RL ->
        let comps' =
            comps
            |> List.collect (fun c->
                match c.Type with
                |Capacitor _ |Inductor _-> []
                |_ -> [c]
            )

        let connsPort0 =
            conns |> List.filter (fun conn -> conn.Target.Id = RL.IOPorts[0].Id || conn.Source.Id = RL.IOPorts[0].Id)
        
        let connsPort1 =
            conns |> List.filter (fun conn -> conn.Target.Id = RL.IOPorts[1].Id || conn.Source.Id = RL.IOPorts[1].Id)
            
        let connsWithoutRL = conns |> List.filter (fun conn -> conn.Target.HostId <> RL.Id && conn.Source.HostId <> RL.Id)
        
        let baseComp0Port = if connsPort0[0].Target.HostId = RL.Id then connsPort0[0].Source else connsPort0[0].Target
        let baseComp1Port = if connsPort1[0].Target.HostId = RL.Id then connsPort1[0].Source else connsPort1[0].Target

        let connsPort0'=
            connsPort0
            |> List.filter (fun conn -> conn.Source.Id <> baseComp0Port.Id || conn.Target.Id <> baseComp0Port.Id)
            |> List.map (fun conn ->
                if conn.Source.Id = RL.IOPorts[0].Id then
                    {conn with Source=baseComp0Port}
                else
                    {conn with Target=baseComp0Port}
            )

        let connsPort1'=
            connsPort1
            |> List.filter (fun conn -> conn.Source.Id <> baseComp1Port.Id || conn.Target.Id <> baseComp1Port.Id)
            |> List.map (fun conn ->
                if conn.Source.Id = RL.IOPorts[1].Id then
                    {conn with Source=baseComp1Port;}
                else
                    {conn with Target=baseComp1Port}
            )

        let basesConnection = {Source=baseComp0Port; Target=baseComp1Port;Vertices=[];Id=JSHelpers.uuid ()}

        let conns' = connsWithoutRL @ connsPort0' @ connsPort1' @ [basesConnection]

        comps',conns'

let replaceCLWithTinyR (comps,conns) =
    //does this work??? 
    let comps' =
            comps
            |> List.map (fun c->
                match c.Type with
                |Capacitor _ |Inductor _ -> {c with Type = Resistor (0.00000001,"0")}
                |_ -> c    
            )
    comps',conns
        
        



let transientAnalysis (comps,conns) inputNode outputNode =
         
    let comps',conns' = combineGrounds (comps,conns)
    
    let findTheveninR node1 node2 =
        let result1,_,_ =
            (comps',conns')
            |> replaceCLWithCS 1.
            |> modifiedNodalAnalysisDC
        
        let result2,_,_ =
            (comps',conns')
            |> replaceCLWithCS 2.
            |> modifiedNodalAnalysisDC

        let v1 =
            if node1=0 then result1[node2-1]
            else if node2=0 then result1[node1-1]
            else result1[node2-1] - result1[node1-1]
        
        let v2 =
            if node1=0 then result2[node2-1]
            else if node2=0 then result2[node1-1]
            else result1[node2-1] - result2[node1-1]

        abs(v2-v1)

    let findTau param =
        let CL = comps' |> List.tryFind (fun c-> match c.Type with |Capacitor _ |Inductor _ -> true |_ ->false)
        match CL with
        |None -> failwithf "No Capacitor/Inductor present in the circuit"
        |Some cl ->
            let (node1,node2) = findNodesOfComp (createNodetoCompsList (comps',conns')) cl
            let Rth = findTheveninR node1 node2
            match cl.Type with
            |Capacitor (c,_) -> Rth*c
            |Inductor (l,_) -> Rth/l
            |_ -> failwithf "No Capacitor/Inductor present in the circuit"

    let findDCGain nodeX nodeY =
        let result,_,_ = modifiedNodalAnalysisDC (comps',conns')
        result[nodeY-1]/result[nodeX-1]
    
    
    let findHFGain nodeX nodeY =
        let comps',conns' = (comps',conns') |> replaceCLWithTinyR //replaceCLWithWire
        let result,_,_ = modifiedNodalAnalysisDC (comps',conns')
        result[nodeY-1]/result[nodeX-1]
    

    // find tau from Thevenin Resistance -> DONE
    // find DC/HF gain -> PENDING for inductor
    // take input signal, find A and plot results
    let vs = comps' |> List.tryFind (fun c->match c.Type with |VoltageSource _ -> true |_->false)

    let yssAsVS =
        match vs with
        |Some comp ->
            match comp.Type with
            |VoltageSource (DC v) ->
                let res,_,_ = modifiedNodalAnalysisDC (comps',conns')
                {comp with Type = VoltageSource (DC res[outputNode-1])}
            |VoltageSource (Sine (a,o,f)) -> 
                let nodeLst = createNodetoCompsList (comps',conns')

                let vs = 
                    comps'
                    |> List.filter (fun c-> match c.Type with |VoltageSource _ -> true |_ -> false)
                    |> List.length

                let opamps = comps' |> List.filter (fun c-> c.Type=Opamp) |> List.length

                let n = List.length nodeLst - 1 + vs + opamps
                let arr = Array.create n 0.0
                let matrix = Array.create n arr
                let vecB =
                    Array.create n {Re=0.0;Im=0.0}
                    |> Array.mapi (fun i v -> (calcVectorBElement (i+1) nodeLst))
                let res = acAnalysis matrix nodeLst vecB (2.*System.Math.PI*f) outputNode |> complexCToP
                printfn "res = %A" res
                {comp with Type = VoltageSource (Sine (a*res.Mag,o+res.Phase,f))}


            |_ -> failwithf "Impossible"
        |None -> failwithf "No Voltage Source present in the circuit"


    let HFGain = findHFGain inputNode outputNode

    let tau = findTau ()
    let f = match yssAsVS.Type with |VoltageSource (Sine (_,_,f)) -> f |_ -> 0.

    let alpha = HFGain * findInputAtTime vs 0. - findInputAtTime (Some yssAsVS) 0.
    
    let dts = if f=0. then [0.0..(tau/10.)..tau*10.] else [0.0..(1./(100.*f))..5./f]

    dts
    |> List.map (fun t->
        let y_tr = alpha*exp(-t/tau)
        let yss = findInputAtTime (Some yssAsVS) t
        (t,y_tr,yss)
    )
    |> List.unzip3
     
    

    