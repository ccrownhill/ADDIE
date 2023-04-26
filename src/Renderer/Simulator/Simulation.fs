﻿module Simulation

open CommonTypes
open MathJsHelpers
open System

/// Helper function to find the components between two nodes
/// using the NodeToCompsList
let findComponentsBetweenNodes (node1:int, node2:int) (nodeToCompsList:(Component*int option) list list) =
    let comps1 = nodeToCompsList[node1] 
    let comps2 = nodeToCompsList[node2]
    comps1
    |> List.filter (fun (c1,no) -> (List.exists (fun (c2:Component,no) -> c1.Id = c2.Id) comps2))

let findNodesOfComp (nodeToCompsList:(Component*int option) list list) comp =
    let pair =
        nodeToCompsList
        |> List.mapi (fun i localNode -> 
            localNode |> List.collect (fun (c,_)->
                match c=comp with
                |true -> [i]
                |false -> []
            )
        )
        |> List.collect id

    match List.length pair with
    |2 when pair[0] <> pair[1] -> (pair[0],pair[1])
    |_ -> failwithf "Wrong parsing of circuit, cannot identify two nodes between a component"

let findConnectionsOnNode (nodeToCompsList:(Component*int option) list list) (index:int) (conns:Connection list) =
    
    let nodeComps = nodeToCompsList[index]
    let nodeCompIds = nodeComps |> List.map (fun (c,pNo) -> c.Id)

    conns
    |> List.filter (fun conn ->
        List.exists (fun id -> id=conn.Source.HostId) nodeCompIds && List.exists (fun id -> id=conn.Target.HostId) nodeCompIds
    )


let findNodeLocation (connsOnNode: Connection list) =
    let extractX (vertix: (float*float*bool)) =
        vertix |> (fun (x,_,_) -> x)
    
    let extractY (vertix: (float*float*bool)) =
        vertix |> (fun (_,y,_) -> y)

    
    let removeDuplicates (vertices: (float*float*bool) list) =
        vertices
        |> List.indexed
        |> List.collect (fun (i,(x,y,z)) -> 
            match i with
            |0 -> [(x,y,z)]
            |_ ->
                if (extractX vertices[i-1] = x && extractY vertices[i-1] = y) then
                    []
                else [(x,y,z)]
        )
        
    let removeSticks (vertices: (float*float*bool) list) =
        vertices
        |> List.removeAt 0
        |> List.removeAt (List.length vertices-2)

    let findMid (vertices: (float*float*bool) list) =
        let mid = List.length vertices / 2
        {X= extractX vertices[mid]; Y=extractY vertices[mid]}

    match List.length connsOnNode with
    |0 -> failwithf "Impossible"
    |1 -> 
        connsOnNode[0].Vertices |> removeDuplicates |> removeSticks |> findMid
    |_ ->
        connsOnNode
        |> List.map (fun x -> x.Vertices |> removeDuplicates |> removeSticks)
        |> List.map ( 
            List.map (fun v -> {X=extractX v; Y= extractY v}))
        |> List.collect id
        |> List.countBy id
        |> List.sortBy (fun (pos,count) -> count)
        |> List.head
        |> fst




/// Given a port on a component, return the other port on this component 
/// (If it exists: Option, otherwise None)
let findOtherEndPort (port:Port) (comps: Component List) : Port option =
    let compOfPort:Component option = List.tryFind (fun c -> c.Id = port.HostId ) comps
    match compOfPort with
    |None -> None
    |Some comp ->
        List.tryFind (fun p -> p.Id <> port.Id) comp.IOPorts


/// Creates a (Component*int option) list list where each element of the top list
/// represents one node and the inner list contains the components
/// that "touch" that node along with the portNumber that touches it (for VS/CS direction). 
let createNodetoCompsList(comps:Component list,conns: Connection list) =
    let ground = 
        match (List.tryFind (fun (c:Component) -> c.Type = Ground) (comps:Component list)) with
        |Some c -> c
        |None -> failwithf "No ground present in the circuit"
   
    /// Function to find the PortNumber of a port which comes from Source/Target of a connection
    let findPortNo (port:Port) (comps:Component list) =
        let comp = comps |> List.find(fun c-> c.Id = port.HostId)
        comp.IOPorts
        |> List.find (fun p -> p.Id = port.Id)
        |> (fun p -> p.PortNumber)
        

    let rec searchCurrentNode (visited:Port list) (toVisitLocal:Port list) (nodeToCompLocalList: (Component*int option) list)=
        match List.isEmpty toVisitLocal with
        |true -> (nodeToCompLocalList,visited)
        |false ->
            let connectedToCurrentPort = 
                List.filter (fun conn -> (conn.Source.Id = toVisitLocal[0].Id || conn.Target.Id = toVisitLocal[0].Id)) conns
                |> List.map(fun c ->
                    match c.Target.Id with
                    |i when i=toVisitLocal[0].Id -> c.Source
                    |_ -> c.Target          
                )
                |> List.collect (fun port -> if (List.exists (fun (x:Port) -> x.Id = port.Id) visited) then [] else [port])

                
            let visited' = List.append visited [toVisitLocal[0]]
            let toVisit' = List.append (List.removeAt 0 toVisitLocal) (connectedToCurrentPort)
            
            let localList' = 
                List.append 
                    nodeToCompLocalList 
                    (List.map (fun p-> 
                        (List.find (fun c->c.Id = p.HostId) comps), (findPortNo p comps) )
                        connectedToCurrentPort
                    )

            searchCurrentNode visited' toVisit' localList'
    
            
    and searchCurrentCanvasState visited (toVisit:Port list) (nodeToCompList:(Component*int option) list list) =

        match List.isEmpty toVisit with
        |true -> List.removeAt 0 nodeToCompList //first element is empty
        |false -> 
            let toVisitFirstComp = List.find (fun (c:Component) -> c.Id = toVisit[0].HostId) comps
            let (newNodeList,visited') = searchCurrentNode visited [toVisit[0]] []
            
            
            let visitedSeq = List.toSeq visited
            let diff = visited' |> List.toSeq |> Seq.except visitedSeq |> Seq.toList
            
            let oppositePorts = 
                List.map (fun port -> findOtherEndPort port comps) diff
                |> List.collect (fun p -> if p = None then [] else [(Option.get p)])
            
            let toVisit' = List.append (List.removeAt 0 toVisit) oppositePorts

            let nodeToCompList' = 
                match List.isEmpty newNodeList with
                |true -> nodeToCompList
                |false -> List.append nodeToCompList [(List.append newNodeList [(toVisitFirstComp, (findPortNo toVisit[0] comps) )])]

            searchCurrentCanvasState visited' toVisit' nodeToCompList'


    searchCurrentCanvasState [] [ground.IOPorts[0]] [[]]


let checkCanvasStateForErrors (comps,conns) =
    let nodeLst = createNodetoCompsList (comps,conns)
    let allCompsOfNodeLst = nodeLst |> List.collect (id) |> List.distinctBy (fun (c,pn) -> c.Id)

    let extractVS comps =
        comps 
        |> List.filter (fun c -> 
            match c.Type with 
            |VoltageSource _ -> true 
            |_ -> false)

    let extractCS comps =
        comps 
        |> List.filter (fun c -> 
            match c.Type with 
            |CurrentSource _ -> true 
            |_ -> false)

    let extractPortIds =
        comps
        |> List.collect (fun c->
            c.IOPorts |> List.collect (fun p -> [p.Id,c])
        )


    let checkGroundExistance =
        match List.exists (fun c->c.Type = Ground) comps with
        |true -> []
        |false ->
            [{
                Msg = sprintf "There is no Ground present in the circuit"
                ComponentsAffected = comps |> List.map (fun c->ComponentId c.Id)
                ConnectionsAffected = []
            }]

    let checkAllCompsConnected =
        comps
        |> List.collect (fun c->
            match List.exists (fun (comp:Component,pn) -> comp.Id = c.Id) allCompsOfNodeLst with
            |true -> []
            |false ->
                [{
                    Msg = sprintf "Component %s is not connected with the main graph" c.Label
                    ComponentsAffected = [ComponentId c.Id]
                    ConnectionsAffected = []
                }]        
        )

    let checkAllPortsConnected =
        extractPortIds
        |> List.collect (fun (pId,comp)->
            let asTarget = List.exists (fun conn -> conn.Target.Id = pId) conns
            let asSource = List.exists (fun conn -> conn.Source.Id = pId) conns
            match asTarget || asSource with
            |true -> []
            |false -> 
                [{
                    Msg = sprintf "Every component port must be connected, but a port of %s is not connected" comp.Label
                    ComponentsAffected = [ComponentId comp.Id]
                    ConnectionsAffected = []
                }] 
       )
 
    let vs = comps |> extractVS
    
    
    let checkNoParallelVS = 
        vs
        |> List.collect (fun comp ->
            let pair = findNodesOfComp nodeLst comp
            let vsOfPair =
                findComponentsBetweenNodes pair nodeLst        
                |> List.map (fst)
                |> extractVS
    
            match List.length vsOfPair with 
            |0 |1 -> []
            |_ ->
                [{
                Msg = sprintf "Voltage sources are connected in parallel between nodes %i and %i" (fst pair) (snd pair)
                ComponentsAffected = vsOfPair |> List.map (fun c->ComponentId c.Id)
                ConnectionsAffected = []
                }]  
            )
    
    let cs = extractCS comps
    let checkNoSeriesCS = 
        nodeLst
        |> List.indexed
        |> List.collect (fun (i, node) ->
            let csOfNode =
                node
                |> List.map fst
                |> extractCS
            
            match (List.length csOfNode) = (List.length node) with 
            |false -> []
            |true ->
                [{
                Msg = sprintf "Current sources are connected in series on node %i" i
                ComponentsAffected = csOfNode |> List.map (fun c->ComponentId c.Id)
                ConnectionsAffected = []
                }]  
            )
    
    let checkNoDoubleConnections =
        let conns'=conns|> List.map (fun c -> {c with Vertices = []})
        List.allPairs conns' conns'
        |> List.collect (fun (c1,c2) ->
            if (c1.Source.Id = c2.Source.Id && c1.Target.Id = c2.Target.Id)
                || (c1.Source.Id = c2.Target.Id && c1.Target.Id = c2.Source.Id) then
                    [{
                    Msg = sprintf "Duplicate connection" 
                    ComponentsAffected = [] 
                    ConnectionsAffected = [ConnectionId c1.Id; ConnectionId c2.Id]
                    }]  
            else 
                []
        )

    let checkNoLoopConns =
        conns
        |> List.collect (fun conn ->
            match conn.Source.HostId = conn.Target.HostId with
            |false -> []
            |true ->
                [{
                Msg = sprintf "Loop connection" 
                ComponentsAffected = [] 
                ConnectionsAffected = [ConnectionId conn.Id]
                }]    
        
        )
 
    checkGroundExistance
    |> List.append checkAllCompsConnected
    |> List.append checkAllPortsConnected
    |> List.append checkNoParallelVS
    |> List.append checkNoSeriesCS
    |> List.append checkNoDoubleConnections
    |> List.append checkNoLoopConns
    






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

        (comps,conns')

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

    let allDCVoltageSources = 
        nodeToCompsList
        |> List.removeAt 0
        |> List.collect (fun nodeComps ->
            nodeComps
            |> List.collect (fun (comp,_) ->
                match comp.Type with
                |VoltageSource (DC (_)) -> [comp]
                |_ -> []
            )
        )
        |> List.distinctBy (fun c->c.Id)
    
    let vsNo = List.length allDCVoltageSources

    if row < nodesNo then
        let currentNode = nodeToCompsList[row]
        findNodeTotalCurrent currentNode
    else if row < (nodesNo + vsNo) then
        allDCVoltageSources[row-nodesNo]
        |> (fun c -> 
            match c.Type with
            |VoltageSource (DC v) -> {Re=v;Im=0.}
            |_ -> failwithf "Impossible"
        )
    else
        {Re=0.;Im=0.}

/// Calculates the value of the MNA matrix at the specified
/// row and column (Count starts from 1)
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
        |VoltageSource (DC _) |Opamp ->
            match comp.Id = vsAndOpamps[col-nodesNo].Id with
            |true ->
                match (comp.Type,no) with
                |VoltageSource (DC (v)),Some 1 -> {Re = -1.0; Im=0.0}
                |VoltageSource (DC (v)), Some 0 -> {Re = 1.0; Im=0.0}
                |Opamp, Some 2 -> {Re = 1.0; Im=0.0}
                |Opamp, _ -> {Re = 0.0; Im=0.0}
                |_ -> failwithf "Unable to identify port of Voltage Source"
            |false -> {Re = 0.0; Im=0.0}
        |_ -> {Re = 0.0; Im=0.0}

    let findMatrixCVoltageValue (comp,no) (vsAndOpamps:Component list) col nodesNo =
        match comp.Type with
        |VoltageSource (DC _) |Opamp ->
            match comp.Id = vsAndOpamps[col-nodesNo].Id with
            |true ->
                match (comp.Type,no) with
                |VoltageSource (DC (v)),Some 1 -> {Re = -1.0; Im=0.0}
                |VoltageSource (DC (v)), Some 0 -> {Re = 1.0; Im=0.0}
                |Opamp, Some 0 -> {Re = 1.0; Im=0.0}
                |Opamp, Some 1 -> {Re = -1.0; Im=0.0}
                |Opamp, _ -> {Re = 0.0; Im=0.0}
                |_ -> failwithf "Unable to identify port of Voltage Source"
            |false -> {Re = 0.0; Im=0.0}
        |_ -> {Re = 0.0; Im=0.0}
                
    let nodesNo = List.length nodeToCompsList
    
    let allDCVoltageSources = 
        nodeToCompsList
        |> List.removeAt 0
        |> List.collect (fun nodeComps ->
            nodeComps
            |> List.collect (fun (comp,_) ->
                match comp.Type with
                |VoltageSource (DC (_)) -> [comp]
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

    let vsNo = List.length allDCVoltageSources

    let vsAndOpamps = allDCVoltageSources @ allOpamps


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
    

    ////////// solve ///////////

    let mul = safeSolveMatrixVec flattenedMatrix vecB

    let result = mul.ToArray() |> Array.map (fun x->System.Math.Round (x,4))
    
    result, nodeLst 


let frequencyResponse (comps,conns) outputNode  =

    let nodeLst = createNodetoCompsList (comps,conns)
    
    let frequencies = [0.0..0.05..7.0] |> List.map (fun x -> 10.**x)

    //printfn "node list %A" nodeLst

    let vs = 
        comps
        |> List.filter (fun c-> match c.Type with |VoltageSource _ -> true |_ -> false)
        |> List.length

    let n = List.length nodeLst - 1 + vs

    let arr = Array.create n 0.0
    let matrix = Array.create n arr
        
    let outputElem = (outputNode*n)-1    
    
    let vecB =
        Array.create n {Re=0.0;Im=0.0}
        |> Array.mapi (fun i v -> (calcVectorBElement (i+1) nodeLst))
    
    frequencies
    |> List.map (fun f->
        let flattenedMatrix =
            matrix
            |> Array.mapi (fun i top ->
                top
                |> Array.mapi (fun j v ->
                    calcMatrixElementValue (i+1) (j+1) nodeLst f vecB
                )
            )
            |> Array.collect (id)
        let inv = safeInvComplexMatrix flattenedMatrix 
        inv[outputElem]
    )
    |> List.map complexCToP
    
