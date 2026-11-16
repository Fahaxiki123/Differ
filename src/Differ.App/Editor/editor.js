"use strict";
// Monaco owns document/undo/input only. All differences and their versions come from the host.
let left, right, diff, original, modified, session, version, projectionVersion=0;
let editMode=false,initialHistoryVersion=0,latestHistoryVersion=0,canUndo=false,canRedo=false,lastStatus="",statusFrame=0;
let currentContext="",approximateHighlight=false;
let lineChanges=[], changedRows=[], decorations=[[],[]], inlineDecorations=[[],[]], focusDecorations=[[],[]];
let findDecorations=[[],[]],findVersion=-1;
let inlineRows=[],zoneRecords=[[],[]],lastPaintedVersion=0,modifiedZoneAnchorsDirty=false;
let zones=[[],[]], hasPendingText=false, pendingText, pendingInline, lastEditAt=0, syncing=false;
let horizontalExtentZones=[null,null],sharedContentWidth=0,pairedScrollTarget=null,pairedScrollFrame=0;
let activeHunk=-1,navigationSequence=0,feedbackFrame=0,feedbackFrames=[],feedbackRails=[],zoneNodes=[new Map(),new Map()];
let navigationLabels={currentBlock:"Block {0} / {1} · {2} · BASE {3} ↔ Result {4}",line:"line {0}",lines:"lines {0}–{1}",missingAtStart:"no lines at file start",missingAfter:"no lines after {0}",notSelected:"Select a block with F7 / F8",noDifferences:"No differences",updating:"Updating differences",added:"Added",deleted:"Deleted",modified:"Modified",unchangedLocation:"{0} · No difference here",resultSide:"Result"};
let pendingFold;
let foldRanges=[[],[]], foldListeners=[], foldSync=false, displayMode=0,foldSignature="",foldModePending=true;
const definedThemePalettes = new Map();
let appliedPalette;
const send=value=>window.chrome.webview.postMessage(value);
require.config({paths:{vs:"vs"}});
require(["vs/editor/editor.main"],()=>{
 // The built-in theme module ships inside editor.main, so it is already defined here.
 builtinThemes=require("vs/editor/standalone/common/themes");
 const settings={automaticLayout:true,readOnly:true,domReadOnly:true,minimap:{enabled:false},fontSize:13.5,lineHeight:24,
  scrollBeyondLastLine:false,smoothScrolling:false,wordWrap:"off",folding:true,renderLineHighlight:"none",
  unicodeHighlight:{ambiguousCharacters:false,nonBasicASCII:false},bracketPairColorization:{enabled:true},
  guides:{indentation:true},stickyScroll:{enabled:false},overviewRulerLanes:0,
  scrollbar:{vertical:"hidden",verticalScrollbarSize:0,alwaysConsumeMouseWheel:false}};
 left=monaco.editor.create(document.getElementById("base"),{...settings,readOnly:true});
 right=monaco.editor.create(document.getElementById("mine"),settings);
 for(const [side,editor] of [[0,left],[1,right]]){
  const frame=document.createElement("div");frame.className="current-hunk-frame";frame.hidden=true;frame.setAttribute("aria-hidden","true");
  editor.getDomNode().parentElement.appendChild(frame);feedbackFrames.push(frame);
  const rail=document.createElement("div");rail.className="current-hunk-rail";rail.hidden=true;rail.setAttribute("aria-hidden","true");
  editor.getDomNode().parentElement.appendChild(rail);feedbackRails.push(rail);
  editor.onDidLayoutChange(()=>{scheduleNavigationFeedback();scheduleDifferenceRail(true);schedulePairedScroll();});
  editor.onDidContentSizeChange(()=>{scheduleDifferenceRail(true);schedulePairedScroll();});
 }
 // This convenience object exposes public standalone-editor APIs to the Windows regression probe.
 diff={getModifiedEditor:()=>right,getOriginalEditor:()=>left};
 const languages=["lua","csharp","javascript","typescript","json","xml","python","cpp","sql","html","css","markdown","yaml","plaintext"];
 for(const language of languages)monaco.languages.registerFoldingRangeProvider(language,{
  onDidChange:listener=>{foldListeners.push(listener);return{dispose:()=>{foldListeners=foldListeners.filter(x=>x!==listener);}};},
  provideFoldingRanges:model=>model===original?foldRanges[0]:model===modified?foldRanges[1]:[]
 });
 for(const editor of [left,right]){
  editor.onDidScrollChange(event=>{
   if(!syncing){
    if(!pairedScrollTarget)pairedScrollTarget={scrollTop:editor.getScrollTop(),scrollLeft:editor.getScrollLeft()};
    else{
     // A geometry notification can report a temporary clamp while the paired extent is being rebuilt.
     if(event.scrollLeftChanged&&!event.scrollWidthChanged&&!event.widthChanged)pairedScrollTarget.scrollLeft=editor.getScrollLeft();
     if(event.scrollTopChanged&&!event.scrollHeightChanged&&!event.heightChanged)pairedScrollTarget.scrollTop=editor.getScrollTop();
    }
    applyPairedScroll();schedulePairedScroll();
   }
   requestInline();scheduleNavigationFeedback();scheduleDifferenceRail();
   if(modified){const r=right.getVisibleRanges();if(r.length)send({type:"viewport",start:(r[0].startLineNumber-1)/modified.getLineCount(),end:r[r.length-1].endLineNumber/modified.getLineCount()});}
  });
  editor.onDidChangeHiddenAreas(()=>{
   scheduleNavigationFeedback();scheduleDifferenceRail(true);if(foldSync)return;
   // Manual expansion is always recoverable; synchronously unfold the paired side as well.
   foldSync=true;Promise.all([left.getAction("editor.unfoldAll").run(),right.getAction("editor.unfoldAll").run()]).finally(()=>foldSync=false);
  });
  editor.addCommand(monaco.KeyCode.F7,()=>send({type:"navigate",direction:-1}));
  editor.addCommand(monaco.KeyCode.F8,()=>send({type:"navigate",direction:1}));
  editor.addCommand(monaco.KeyMod.CtrlCmd|monaco.KeyCode.KeyW,()=>send({type:"close"}));
  editor.addCommand(monaco.KeyMod.CtrlCmd|monaco.KeyCode.KeyS,()=>{flushText();send({type:"save",session,version});});
  editor.addCommand(monaco.KeyMod.CtrlCmd|monaco.KeyMod.Shift|monaco.KeyCode.KeyS,()=>{flushText();send({type:"saveAs",session,version});});
  editor.addCommand(monaco.KeyMod.CtrlCmd|monaco.KeyMod.Alt|monaco.KeyCode.RightArrow,adoptCurrent);
 }
 right.onDidChangeCursorPosition(e=>{
  const line=e.position.lineNumber;
  const i=lineChanges.findIndex(c=>line>=Math.max(1,c.modifiedStartLineNumber)&&line<=Math.max(c.modifiedStartLineNumber,c.modifiedEndLineNumber));
  if(i>=0&&projectionVersion===version)selectHunk(i,false);
  updateContext(line);
 });
 initializeDifferenceRail();
 document.getElementById("adopt-current").addEventListener("click",adoptCurrent);
 document.addEventListener("keydown", event => {
  if (event.key !== "F9" || event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return;
  event.preventDefault();
  event.stopImmediatePropagation();
  if (!event.repeat) send({type:"toggleTheme"});
 }, true);
 send({type:"ready"});
});
function schedulePairedScroll(){
 if(!pairedScrollFrame)pairedScrollFrame=requestAnimationFrame(()=>{pairedScrollFrame=0;applyPairedScroll();});
}
function applyPairedScroll(){
 if(syncing||!left?.getModel()||!right?.getModel())return;
 syncing=true;
 try{
  const editors=[left,right],target=pairedScrollTarget||{scrollTop:right.getScrollTop(),scrollLeft:right.getScrollLeft()};
  const viewportWidth=Math.min(...editors.map(editor=>editor.getLayoutInfo().contentWidth));
  for(const [side,editor]of editors.entries()){
   const measured=editor.getScrollWidth(),injected=horizontalExtentZones[side]?.width||0;
   // Do not treat our own extent, or a resized viewport, as newly measured document width.
   if(measured>injected&&measured>editor.getLayoutInfo().contentWidth)sharedContentWidth=Math.max(sharedContentWidth,measured);
  }
  const maximum=Math.max(0,sharedContentWidth-viewportWidth);
  target.scrollLeft=Math.min(target.scrollLeft,maximum);
  pairedScrollTarget=target;
  for(const [side,editor]of editors.entries()){
   const width=Math.ceil(maximum+editor.getLayoutInfo().contentWidth),previous=horizontalExtentZones[side];
   if(maximum>0&&previous?.width!==width){
    // A zero-height public view zone shares the measured horizontal extent without altering either model.
    editor.changeViewZones(accessor=>{
     if(previous)accessor.removeZone(previous.id);
     const id=accessor.addZone({afterLineNumber:0,heightInPx:0,minWidthInPx:width,domNode:document.createElement("div"),suppressMouseDown:true});
     horizontalExtentZones[side]={id,width};
    });
   }
   editor.setScrollPosition(target,monaco.editor.ScrollType.Immediate);
  }
 }finally{syncing=false;}
}
function resetPairedScroll(){
 if(pairedScrollFrame)cancelAnimationFrame(pairedScrollFrame);
 pairedScrollFrame=0;pairedScrollTarget=null;sharedContentWidth=0;
 const previousSyncing=syncing;syncing=true;
 try{for(const [side,editor]of [[0,left],[1,right]]){
  const zone=horizontalExtentZones[side];
  if(zone&&editor)editor.changeViewZones(accessor=>accessor.removeZone(zone.id));
  horizontalExtentZones[side]=null;
 }}finally{syncing=previousSyncing;}
}
function flushText(){clearTimeout(pendingText);if(!hasPendingText)return;hasPendingText=false;version++;send({type:"text",session,version,text:modified.getValue(monaco.editor.EndOfLinePreference.LF)});publishStatus();}
function updateContext(line){
 if(!modified)return;currentContext="";
 if(modified.getLanguageId()==="lua")for(let i=line-1;i>=Math.max(0,line-500);i--){const text=modified.getLineContent(i+1);if(/^\s*(local\s+)?function\s+/.test(text)||/^\s*\w[\w.:]*\s*=\s*function\b/.test(text)){currentContext=text.trim();break;}}
 publishStatus();
}
function publishStatus(){
 if(statusFrame)return;
 statusFrame=requestAnimationFrame(()=>{
  statusFrame=0;if(!modified||!session)return;
  let location=document.getElementById("current-location").textContent;
  if(activeHunk>=0&&projectionVersion===version&&!hasPendingText){const c=lineChanges[activeHunk];location=formatLabel(navigationLabels.location||"BASE {0} ↔ Result {1}",rangeLabel(sideRange(c,0)),rangeLabel(sideRange(c,1)));}
  const message={type:"status",session,version,location,context:approximateHighlight?"当前长行部分范围为粗略高亮":currentContext,language:modified.getLanguageId(),canUndo,canRedo};
  const encoded=JSON.stringify(message);if(encoded!==lastStatus){lastStatus=encoded;send(message);}
 });
}
function adoptCurrent(){if(editMode&&!hasPendingText&&projectionVersion===version&&activeHunk>=0)send({type:"adopt",session,version,index:activeHunk});}
function requestInline(){clearTimeout(pendingInline);pendingInline=setTimeout(()=>{if(projectionVersion!==version||hasPendingText||!modified)return;const a=left.getVisibleRanges(),b=right.getVisibleRanges();if(!a.length||!b.length)return;send({type:"inlineRequest",version,oldStart:a[0].startLineNumber,oldEnd:a[a.length-1].endLineNumber,newStart:b[0].startLineNumber,newEnd:b[b.length-1].endLineNumber});},80);}
function clearProjection(){
 clearFindFeedback();
 for(const [i,editor] of [[0,left],[1,right]]){decorations[i]=editor.deltaDecorations(decorations[i],[]);inlineDecorations[i]=editor.deltaDecorations(inlineDecorations[i],[]);editor.changeViewZones(accessor=>{for(const id of zones[i])accessor.removeZone(id);zones[i]=[];});}
 clearNavigationFeedback();zoneNodes=[new Map(),new Map()];zoneRecords=[[],[]];inlineRows=[];lastPaintedVersion=0;foldSignature="";modifiedZoneAnchorsDirty=false;
 resetDifferenceRail();
}
function pruneInlineRows(currentPairs){
 const removed=[new Set(),new Set()];
 inlineRows=inlineRows.filter(row=>{
  const ranges=row.anchors.map((id,side)=>(side===0?original:modified).getDecorationRange(id));
  const valid=ranges.every((range,side)=>range&&range.startLineNumber===range.endLineNumber&&(side===0?original:modified).getLineContent(range.startLineNumber)===row.text[side])
   &&(!currentPairs||currentPairs.has(`${ranges[0].startLineNumber}:${ranges[1].startLineNumber}`));
  if(!valid)for(const side of [0,1])for(const id of row.ids[side])removed[side].add(id);
  return valid;
 });
 for(const [side,editor]of [[0,left],[1,right]])if(removed[side].size){editor.deltaDecorations([...removed[side]],[]);inlineDecorations[side]=inlineDecorations[side].filter(id=>!removed[side].has(id));}
}
function updateAlignmentZones(){
 let changed=false;zoneNodes=[new Map(),new Map()];
 for(const [side,editor]of [[0,left],[1,right]]){
  const available=new Map(),atLine=new Map(),used=new Set();
  for(const record of zoneRecords[side]){
   const key=`${record.after}:${record.height}`;if(!available.has(key))available.set(key,[]);available.get(key).push(record);
   if(!atLine.has(record.after))atLine.set(record.after,[]);atLine.get(record.after).push(record);
  }
  const take=items=>{while(items?.length){const record=items.shift();if(!used.has(record)){used.add(record);return record;}}};
  const desired=[];
  for(const [index,c]of lineChanges.entries()){
   const oldCount=c.originalEndLineNumber===0?0:c.originalEndLineNumber-c.originalStartLineNumber+1;
   const newCount=c.modifiedEndLineNumber===0?0:c.modifiedEndLineNumber-c.modifiedStartLineNumber+1;
   const height=side===0?newCount-oldCount:oldCount-newCount;
   if(height<=0)continue;
   const after=side===0?(c.originalEndLineNumber||c.originalStartLineNumber):(c.modifiedEndLineNumber||c.modifiedStartLineNumber);
   const previous=take(available.get(`${after}:${height}`));
   const record=previous||{after,height,node:document.createElement("div"),id:null};
   // Monaco shifts its internal whitespace anchors on structural model edits.
   // Our cached delegate is not shifted, so even an exact key must be reapplied.
   record.needsLayout=!!previous&&(record.height!==height||side===1&&modifiedZoneAnchorsDirty);record.after=after;record.height=height;record.index=index;
   if(!previous){record.node.className="missing-zone";record.zone={afterLineNumber:after,heightInLines:height,domNode:record.node,suppressMouseDown:true};}
   record.node.setAttribute("aria-label",`${height} 行无对应内容`);record.zone.heightInLines=height;
   desired.push(record);zoneNodes[side].set(index,record.node);
  }
  const reuse=(record,next)=>{
   record.needsLayout=true;record.after=next.after;record.height=next.height;record.index=next.index;
   record.zone.afterLineNumber=record.after;record.zone.heightInLines=record.height;
   record.node.setAttribute("aria-label",`${record.height} 行无对应内容`);zoneNodes[side].set(record.index,record.node);
   return record;
  };
  // Reserve every exact match before considering resized or moved spacers.
  for(let i=0;i<desired.length;i++)if(desired[i].id===null){const record=take(atLine.get(desired[i].after));if(record)desired[i]=reuse(record,desired[i]);}
  const obsolete=zoneRecords[side].filter(record=>!used.has(record));
  // A moved gap is still the same kind of noninteractive spacer. Relayout its
  // existing node instead of briefly replacing it with an unmeasured new node.
  for(let i=0;i<desired.length&&obsolete.length;i++)if(desired[i].id===null){
   desired[i]=reuse(obsolete.shift(),desired[i]);
  }
  if(obsolete.length||desired.some(record=>record.id===null||record.needsLayout)){
   changed=true;
   editor.changeViewZones(accessor=>{
    for(const record of obsolete)accessor.removeZone(record.id);
    for(const record of desired){if(record.id===null)record.id=accessor.addZone(record.zone);else if(record.needsLayout)accessor.layoutZone(record.id);record.needsLayout=false;}
   });
  }
  zoneRecords[side]=desired;zones[side]=desired.map(record=>record.id);
 }
 modifiedZoneAnchorsDirty=false;
 return changed;
}
function applyProjection(m){
 if(m.version!==version||hasPendingText)return;
 const visible=right.getVisibleRanges()[0],anchor=visible?{line:visible.startLineNumber,offset:right.getTopForLineNumber(visible.startLineNumber)-right.getScrollTop()}:null;
 // The models already contain the edit. Replace their tracked paint in one turn;
 // clearing it first exposes plain syntax colours between every pair of keystrokes.
 clearNavigationFeedback();
 lineChanges=m.changes;changedRows=m.rows;projectionVersion=version;
 const dec=[[],[]],previous=[null,null];
 for(const [rowIndex,row] of changedRows.entries())for(const [side,line] of [[0,row.oldLine],[1,row.newLine]])if(line!==null){
  const wholeChange=row.kind==="added"||row.kind==="deleted",model=side===0?original:modified;
  const prior=previous[side],continues=prior&&prior.line===line-1&&prior.index===rowIndex-1&&prior.kind===row.kind;
  if(continues)prior.decoration.options.linesDecorationsClassName=prior.decoration.options.linesDecorationsClassName.replace(" rail-end","");
  const decoration={range:new monaco.Range(line,1,line,wholeChange?model.getLineMaxColumn(line):1),options:{isWholeLine:true,className:`diff-line ${row.kind}`,linesDecorationsClassName:`diff-gutter ${row.kind}${continues?"":" rail-start"} rail-end`,inlineClassName:wholeChange?(side===0?"changed-old":"changed-new"):undefined}};
  dec[side].push(decoration);previous[side]={line,index:rowIndex,kind:row.kind,decoration};
 }
 for(const [i,editor] of [[0,left],[1,right]])decorations[i]=editor.deltaDecorations(decorations[i],dec[i]);
 const pairs=new Set(changedRows.filter(row=>row.oldLine!==null&&row.newLine!==null).map(row=>`${row.oldLine}:${row.newLine}`));
 // Same-version projections may be caused by changed comparison options.
 pruneInlineRows(lastPaintedVersion===version?new Set():pairs);lastPaintedVersion=version;
 syncing=true;
 const zonesChanged=updateAlignmentZones();
 if(zonesChanged&&anchor)right.setScrollTop(right.getTopForLineNumber(Math.min(anchor.line,modified.getLineCount()))-anchor.offset,monaco.editor.ScrollType.Immediate);
 if(zonesChanged)left.setScrollPosition({scrollTop:right.getScrollTop(),scrollLeft:right.getScrollLeft()});
 if(zonesChanged)pairedScrollTarget={scrollTop:right.getScrollTop(),scrollLeft:right.getScrollLeft()};
 syncing=false;
 setFoldingRegions();
 renderCurrentLocation();requestInline();scheduleDifferenceRail(true);
 if(lastEditAt)send({type:"metric",operation:"editor-diff-ready",elapsedMs:performance.now()-lastEditAt});
 requestAnimationFrame(()=>send({type:"rendered",version}));
}
function setFoldingRegions(){
 if(!original||!modified||projectionVersion!==version||hasPendingText)return;
 const next=[[],[]];let oldPrevious=1,newPrevious=1;const context=displayMode===1?0:3;
 for(const c of [...lineChanges,{originalStartLineNumber:original.getLineCount()+1,modifiedStartLineNumber:modified.getLineCount()+1,originalEndLineNumber:0,modifiedEndLineNumber:0}]){
  const oldStart=c.originalEndLineNumber===0?c.originalStartLineNumber+1:c.originalStartLineNumber;
  const newStart=c.modifiedEndLineNumber===0?c.modifiedStartLineNumber+1:c.modifiedStartLineNumber;
  const count=Math.min(oldStart-oldPrevious,newStart-newPrevious);
  if(count>context*2+3){next[0].push({start:oldPrevious+context,end:oldStart-context-1});next[1].push({start:newPrevious+context,end:newStart-context-1});}
  oldPrevious=(c.originalEndLineNumber||c.originalStartLineNumber)+1;newPrevious=(c.modifiedEndLineNumber||c.modifiedStartLineNumber)+1;
 }
 const signature=JSON.stringify(next);
 if(signature!==foldSignature){foldRanges=next;foldSignature=signature;for(const listener of foldListeners)listener();}
 if(foldModePending){
  foldModePending=false;clearTimeout(pendingFold);
  if(displayMode!==0)pendingFold=setTimeout(()=>{if(projectionVersion!==version||hasPendingText)return;foldSync=true;Promise.all([left.getAction("editor.foldAll").run(),right.getAction("editor.foldAll").run()]).finally(()=>foldSync=false);},180);
 }
}
function applyInline(m){
 if(m.version!==version||projectionVersion!==version||hasPendingText)return;const dec=[[],[]];
 const rows=[];
 for(const row of m.rows){
  // Pure insertions/deletions are styled with the stable row paint above.
  if(row.oldLine===null||row.newLine===null)continue;
  const record={anchors:[],ids:[[],[]],text:[],indices:[[],[]]};
  for(const [side,line,segments]of [[0,row.oldLine,row.oldSegments],[1,row.newLine,row.newSegments]]){
   const model=side===0?original:modified;record.text[side]=model.getLineContent(line);
   record.indices[side].push(dec[side].length);
   dec[side].push({range:new monaco.Range(line,1,line,model.getLineMaxColumn(line)),options:{description:"differ-inline-row",stickiness:monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges}});
   let column=1;for(const segment of segments){if(segment.changed&&segment.length){record.indices[side].push(dec[side].length);dec[side].push({range:new monaco.Range(line,column,line,column+segment.length),options:{inlineClassName:side===0?"changed-old":"changed-new"}});}column+=segment.length;}
  }
  rows.push(record);
 }
 for(const [i,editor]of [[0,left],[1,right]])inlineDecorations[i]=editor.deltaDecorations(inlineDecorations[i],dec[i]);
 for(const record of rows)for(const side of [0,1]){record.ids[side]=record.indices[side].map(index=>inlineDecorations[side][index]);record.anchors[side]=record.ids[side][0];}
 inlineRows=rows;
 approximateHighlight=m.rows.some(row=>row.approximate);publishStatus();
}
function formatLabel(template,...values){return template.replace(/\{(\d+)\}/g,(_,index)=>String(values[Number(index)]));}
function sideRange(change,side){return side===0?{start:change.originalStartLineNumber,end:change.originalEndLineNumber}:{start:change.modifiedStartLineNumber,end:change.modifiedEndLineNumber};}
function rangeLabel(range){return range.end===0?(range.start===0?navigationLabels.missingAtStart:formatLabel(navigationLabels.missingAfter,range.start)):range.start===range.end?formatLabel(navigationLabels.line,range.start):formatLabel(navigationLabels.lines,range.start,range.end);}
function renderCurrentLocation(){
 publishStatus();scheduleDifferenceRail();
 const label=document.getElementById("current-location");label.title="";
 if(hasPendingText){label.textContent=navigationLabels.updating;return;}
 if(activeHunk<0||activeHunk>=lineChanges.length){label.textContent=projectionVersion===version&&lineChanges.length===0?navigationLabels.noDifferences:navigationLabels.notSelected;return;}
 const change=lineChanges[activeHunk],kind=change.originalEndLineNumber===0?navigationLabels.added:change.modifiedEndLineNumber===0?navigationLabels.deleted:navigationLabels.modified;
 label.textContent=formatLabel(navigationLabels.currentBlock,activeHunk+1,lineChanges.length,kind,rangeLabel(sideRange(change,0)),rangeLabel(sideRange(change,1)));
 label.title=label.textContent;
}
function clearNavigationFeedback(){
 clearTimeout(pendingFold);navigationSequence++;activeHunk=-1;
 for(const [side,editor]of [[0,left],[1,right]])focusDecorations[side]=editor.deltaDecorations(focusDecorations[side],[]);
 for(const frame of feedbackFrames){frame.hidden=true;frame.classList.remove("locating");}
 for(const rail of feedbackRails)rail.hidden=true;
 const adopt=document.getElementById("adopt-current");
 adopt.hidden=true;
 adopt.disabled=true;
 renderCurrentLocation();
}
function scheduleNavigationFeedback(){if(!feedbackFrame)feedbackFrame=requestAnimationFrame(()=>{feedbackFrame=0;positionNavigationFeedback();});}
function positionNavigationFeedback(){
 const adopt=document.getElementById("adopt-current");
 const current=activeHunk>=0&&projectionVersion===version&&!hasPendingText;
 adopt.disabled=!editMode||!current;
 if(!current){
  adopt.hidden=true;
  for(const frame of [...feedbackFrames,...feedbackRails])frame.hidden=true;
  return;
 }
 const change=lineChanges[activeHunk];
 let adoptCenter=null;
 for(const [side,editor]of [[0,left],[1,right]]){
  const frame=feedbackFrames[side],rail=feedbackRails[side],range=sideRange(change,side),layout=editor.getLayoutInfo(),root=editor.getDomNode().getBoundingClientRect();
  const zone=zoneNodes[side].get(activeHunk),zoneBox=zone?.getBoundingClientRect();
  let top,bottom;
  if(range.end===0){
   if(!zoneBox||zoneBox.height<=0){frame.hidden=true;rail.hidden=true;continue;}
   top=zoneBox.top-root.top;bottom=top+zoneBox.height;
  }else{
   top=editor.getTopForLineNumber(range.start)-editor.getScrollTop();
   bottom=editor.getBottomForLineNumber(range.end)-editor.getScrollTop();
   if(zoneBox&&zoneBox.height>0)bottom=Math.max(bottom,zoneBox.bottom-root.top);
  }
  const visibleTop=Math.max(0,top),visibleBottom=Math.min(layout.height,bottom);
  frame.hidden=visibleBottom<=visibleTop;
  frame.style.left=`${layout.contentLeft}px`;frame.style.width=`${Math.max(0,layout.contentWidth)}px`;
  frame.style.top=`${visibleTop}px`;frame.style.height=`${Math.max(0,visibleBottom-visibleTop)}px`;
  rail.hidden=frame.hidden;
  const railTop=Math.max(0,top+1),railBottom=Math.min(layout.height,bottom-1);
  rail.hidden=rail.hidden||railBottom<=railTop;
  rail.style.left=`${layout.decorationsLeft}px`;rail.style.top=`${railTop}px`;rail.style.height=`${Math.max(0,railBottom-railTop)}px`;
  rail.style.borderRadius=`${top>=0?2.5:0}px ${top>=0?2.5:0}px ${bottom<=layout.height?2.5:0}px ${bottom<=layout.height?2.5:0}px`;
  frame.dataset.start=String(range.start);frame.dataset.end=String(range.end);frame.dataset.block=String(activeHunk+1);
  if(!adopt.disabled&&!frame.hidden&&adoptCenter===null)
   adoptCenter=visibleTop+editor.getOption(monaco.editor.EditorOption.lineHeight)/2;
 }
 adopt.hidden=adoptCenter===null;
 if(adoptCenter!==null){
  adopt.style.setProperty("--adopt-center",`${adoptCenter}px`);
  adopt.dataset.block=String(activeHunk+1);
 }
}
function flashNavigationFeedback(){
 for(const frame of feedbackFrames){frame.classList.remove("locating");void frame.offsetWidth;frame.classList.add("locating");}
}
function revealPaired(editor,line,end=line,sequence=++navigationSequence,findRange=null){
 clearTimeout(pendingFold);const requestedSession=session,requestedVersion=version;foldSync=true;
 Promise.all([left.getAction("editor.unfoldAll").run(),right.getAction("editor.unfoldAll").run()]).then(()=>{
  if(sequence!==navigationSequence||requestedSession!==session||requestedVersion!==version||hasPendingText)return;
  if(findRange)editor.revealRangeInCenter(findRange,monaco.editor.ScrollType.Immediate);
  else editor.revealLinesInCenter(Math.max(1,line),Math.max(1,end),monaco.editor.ScrollType.Immediate);
  requestAnimationFrame(()=>{
   if(sequence!==navigationSequence||requestedSession!==session||requestedVersion!==version||hasPendingText)return;
   if(findRange){
    // Automatic layout may reveal the other editor's old caret after the first range reveal.
    // Commit this still-current navigation once both editors have rendered the new geometry.
    editor.revealRangeInCenter(findRange,monaco.editor.ScrollType.Immediate);
    pairedScrollTarget={scrollTop:editor.getScrollTop(),scrollLeft:editor.getScrollLeft()};
    applyPairedScroll();
   }
   positionNavigationFeedback();flashNavigationFeedback();
  });
 }).finally(()=>foldSync=false);
}
function selectHunk(index,reveal){
 if(projectionVersion!==version||hasPendingText||index<0||index>=lineChanges.length)return;
 const sequence=++navigationSequence;clearTimeout(pendingFold);activeHunk=index;const change=lineChanges[index];
 for(const [side,editor]of [[0,left],[1,right]]){
  const range=sideRange(change,side),anchor=Math.max(1,Math.min(editor.getModel().getLineCount(),range.start));
  focusDecorations[side]=editor.deltaDecorations(focusDecorations[side],[{range:new monaco.Range(anchor,1,Math.max(anchor,range.end),1),options:{isWholeLine:true,linesDecorationsClassName:"current-hunk-gutter",zIndex:30}}]);
 }
 renderCurrentLocation();scheduleNavigationFeedback();
 if(reveal){const side=change.modifiedEndLineNumber===0?0:1,range=sideRange(change,side);revealPaired(side===0?left:right,range.start,range.end,sequence);}
 send({type:"selected",session,version,index});
}
function revealLocation(mine,line,findRange=null){
 if(projectionVersion!==version||hasPendingText)return; const side=mine?1:0,index=lineChanges.findIndex(change=>{const range=sideRange(change,side);return range.end>0&&line>=range.start&&line<=range.end;});
 if(index>=0)selectHunk(index,false);
 else{
  clearNavigationFeedback();send({type:"selected",session,version,index:-1});
  document.getElementById("current-location").textContent=formatLabel(navigationLabels.unchangedLocation,`${mine?navigationLabels.resultSide:"BASE"} ${formatLabel(navigationLabels.line,line)}`);
  publishStatus();
 }
 revealPaired(mine?right:left,line,line,++navigationSequence,findRange);
}
function clearFindFeedback(){
 for(const [side,editor]of [[0,left],[1,right]])if(editor)findDecorations[side]=editor.deltaDecorations(findDecorations[side],[]);
}
function revealFind(m){
 if(m.session!==session||m.version!==version||projectionVersion!==version||hasPendingText||!Number.isInteger(m.findVersion)||m.findVersion<findVersion)return;
 const side=m.mine?1:0,editor=m.mine?right:left,model=editor.getModel();
 if(!model||!Number.isInteger(m.line)||!Number.isInteger(m.column)||!Number.isInteger(m.length)||m.length<=0||m.line<1||m.line>model.getLineCount())return;
 if(m.column<1||m.column+m.length>model.getLineMaxColumn(m.line))return;
 const range=new monaco.Range(m.line,m.column,m.line,m.column+m.length);
 findVersion=m.findVersion;clearFindFeedback();
 findDecorations[side]=editor.deltaDecorations(findDecorations[side],[{range,options:{className:"host-find-match",stickiness:monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,zIndex:40}}]);
 // Decorations reveal the range without changing the selection or taking focus from the host find box.
 revealLocation(m.mine,m.line,range);
}
window.chrome.webview.addEventListener("message",event=>{
 const m=event.data;if(!right)return;
 switch(m.type){
 case "load":
  stopThemeMist();finishThemeColors();
  resetPairedScroll();
  clearTimeout(pendingText);hasPendingText=false;clearProjection();if(original)original.dispose();if(modified)modified.dispose();
  navigationLabels=m.navigationLabels||navigationLabels;session=m.session;version=m.version;findVersion=-1;projectionVersion=0;lineChanges=[];foldModePending=true;editMode=!!m.editMode;canUndo=canRedo=false;lastStatus="";currentContext="";approximateHighlight=false;
  differenceRail.setAttribute("aria-label",navigationLabels.overviewRail||"差异滚动条");
  document.getElementById("adopt-current").title=navigationLabels.adoptCurrent||"Use left block";
  document.getElementById("adopt-current").setAttribute("aria-label",navigationLabels.adoptCurrent||"Use left block");
  original=monaco.editor.createModel(m.original,m.language);modified=monaco.editor.createModel(m.modified,m.language);
  original.setEOL(monaco.editor.EndOfLineSequence.LF);modified.setEOL(monaco.editor.EndOfLineSequence.LF);
  left.setModel(original);right.setModel(modified);resetPairedScroll();left.updateOptions({readOnlyMessage:{value:navigationLabels.readOnlyBase}});right.updateOptions({readOnly:!editMode,domReadOnly:!editMode,readOnlyMessage:{value:navigationLabels.readOnlyMine}});setTheme(m);
  initialHistoryVersion=latestHistoryVersion=modified.getAlternativeVersionId();
  modified.onDidChangeContent(e=>{
   if(e.changes.some(change=>change.range.startLineNumber!==change.range.endLineNumber||/[\r\n]/.test(change.text)))modifiedZoneAnchorsDirty=true;
   const historyVersion=modified.getAlternativeVersionId();canUndo=historyVersion!==initialHistoryVersion;
   if(e.isUndoing)canRedo=true;else if(e.isRedoing)canRedo=historyVersion!==latestHistoryVersion;else{canRedo=false;latestHistoryVersion=historyVersion;}
   lastEditAt=performance.now();projectionVersion=0;lineChanges=[];approximateHighlight=false;pruneInlineRows();clearNavigationFeedback();clearFindFeedback();resetDifferenceRail();if(!hasPendingText){hasPendingText=true;send({type:"pending",session});renderCurrentLocation();}clearTimeout(pendingText);pendingText=setTimeout(flushText,100);publishStatus();requestAnimationFrame(()=>send({type:"metric",operation:"editor-input-frame",elapsedMs:performance.now()-lastEditAt}));
  });updateContext(1);renderCurrentLocation();break;
 case "editMode":
  if(m.session!==session)break;
  // One renderer turn freezes input before sending its final document batch and acknowledgement.
  editMode=!!m.enabled;right.updateOptions({readOnly:!editMode,domReadOnly:!editMode});flushText();
  positionNavigationFeedback();publishStatus();send({type:"modeChanged",id:m.id,session,version});break;
 case "clear":
  stopThemeMist();finishThemeColors();
  resetPairedScroll();
  editMode=false;right.updateOptions({readOnly:true,domReadOnly:true});clearTimeout(pendingText);hasPendingText=false;clearProjection();left.setModel(null);right.setModel(null);resetPairedScroll();original?.dispose();modified?.dispose();original=modified=null;session=null;break;
 case "projection":applyProjection(m);break;
 case "projectionFailed":
  if(m.session!==session||m.version!==version||hasPendingText)break;
  projectionVersion=0;lineChanges=[];changedRows=[];clearProjection();foldRanges=[[],[]];for(const listener of foldListeners)listener();
  foldSync=true;Promise.all([left.getAction("editor.unfoldAll").run(),right.getAction("editor.unfoldAll").run()]).finally(()=>foldSync=false);break;
 case "inline":applyInline(m);break;
 case "theme":applyThemeTransition(m);break;
 case "themeTransitionStart":startPreparedTheme(m);break;
 case "themeMistStop":stopThemeMist();finishThemeColors(m.completePalette !== false);break;
 case "revealHunk":if(m.version===version)selectHunk(m.index,true);break;
 case "reveal":if(m.version===version)revealLocation(m.mine,m.line);break;
 case "revealFind":revealFind(m);break;
 case "clearFind":if(m.session===session&&Number.isInteger(m.findVersion)&&m.findVersion>=findVersion){findVersion=m.findVersion;navigationSequence++;clearFindFeedback();}break;
 case "replace":if(!editMode||m.version!==version||projectionVersion!==version||hasPendingText)break;const a=modified.getPositionAt(m.offset),b=modified.getPositionAt(m.offset+m.length);right.pushUndoStop();right.executeEdits("differ.adopt",[{range:new monaco.Range(a.lineNumber,a.column,b.lineNumber,b.column),text:m.text,forceMoveMarkers:true}]);right.pushUndoStop();right.focus();break;
 case "undo":if(editMode){right.focus();right.trigger("differ","undo",null);}break;
 case "redo":if(editMode){right.focus();right.trigger("differ","redo",null);}break;
 case "focus":(m.mine?right:left).focus();break;
 case "scroll":if(m.fraction!==null)right.revealLineInCenter(Math.max(1,Math.round(m.fraction*modified.getLineCount())));else right.setScrollTop(right.getScrollTop()-m.delta);break;
 case "find":right.trigger("differ","actions.find",null);break;
 case "flush":flushText();send({type:"flushed",id:m.id,session,version});break;
 case "display":displayMode=m.mode;foldModePending=true;if(displayMode===0){foldSync=true;Promise.all([left.getAction("editor.unfoldAll").run(),right.getAction("editor.unfoldAll").run()]).finally(()=>foldSync=false);}setFoldingRegions();break;
 }
});
function setTheme(m){
 if(m.palette){
  const p = m.palette;
  registerPaletteColors(p);
  const theme = m.dark ? "differ-dark" : "differ-light";
  const colors = {
   "editor.background": p.background, "editor.foreground": p.foreground, "editorGutter.background": p.background,
   "editorLineNumber.foreground": p.lineNumber, "editorLineNumber.activeForeground": p.focus,
   "editorCursor.foreground": p.focus, "editor.selectionBackground": p.selection, "editor.inactiveSelectionBackground": p.inactiveSelection,
   "editorIndentGuide.background1": p.indent, "editorIndentGuide.activeBackground1": p.border,
   "scrollbarSlider.background": p.scrollThumb, "scrollbarSlider.hoverBackground": p.scrollHover, "scrollbarSlider.activeBackground": p.scrollPressed,
   "editorWidget.background": p.chrome, "editorWidget.border": p.border, "editorWidget.foreground": p.foreground, "focusBorder": p.focus,
   "input.background": p.background, "input.foreground": p.foreground, "input.border": p.border
  };
  const signature = JSON.stringify(colors);
  if (definedThemePalettes.get(theme) !== signature) {
   const builtin = builtinThemes[m.dark ? "vs_dark" : "vs"];
   monaco.editor.defineTheme(theme, {base: m.dark ? "vs-dark" : "vs", inherit: false,
    rules: mergedTokenRules(m.dark, p.foreground, p.background), colors: {...builtin.colors, ...colors}});
   definedThemePalettes.set(theme, signature);
  }
  monaco.editor.setTheme(theme);

  let colorsChanged = false;
  for (const [name, value] of Object.entries(p)) {
   if (typeof value === "string" && value !== appliedPalette?.[name]) {
    document.documentElement.style.setProperty("--" + name, value);
    colorsChanged = true;
   }
  }
  if (p.fontSize && (p.fontSize !== appliedPalette?.fontSize || p.lineHeight !== appliedPalette?.lineHeight)) {
   const scrollBeforeScale=pairedScrollTarget||{scrollTop:right.getScrollTop(),scrollLeft:right.getScrollLeft()};
   resetPairedScroll();
   const previousSyncing=syncing;syncing=true;
   try{
    for (const [side, editor] of [[0,left],[1,right]]) {
     const previousLineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
     editor.updateOptions({fontSize: p.fontSize, lineHeight: p.lineHeight || previousLineHeight});
     // Monaco normalizes fractional line heights before laying out the existing spacers.
     if (previousLineHeight !== editor.getOption(monaco.editor.EditorOption.lineHeight)) {
      editor.changeViewZones(accessor => { for (const record of zoneRecords[side]) accessor.layoutZone(record.id); });
     }
     editor.render(true);
    }
   }finally{syncing=previousSyncing;pairedScrollTarget=scrollBeforeScale;schedulePairedScroll();}
   scheduleNavigationFeedback();
   scheduleDifferenceRail(true);
  } else if (colorsChanged) {
   scheduleDifferenceRailRepaint();
  }
  appliedPalette = p;
 }else monaco.editor.setTheme(m.dark?"vs-dark":"vs");
 document.body.classList.toggle("dark",m.dark);
}

// Light and dark use one rule list over the union of Monaco's built-in scopes. Every distinct
// (light, dark) colour pair gets its own colour in both themes, so each token keeps the same
// .mtkN class across a switch. A class-level bridge also covers spans Monaco replaces while repainting.
let builtinThemes;
let tokenScopePlan;

function resolveBuiltinRule(rules, scope, field) {
    for (let name = scope; ; name = name.includes(".") ? name.slice(0, name.lastIndexOf(".")) : "") {
        const value = rules.get(name)?.[field];
        if (value !== undefined) return name === "" && field === "foreground" ? null : value;
        if (name === "") return null;
    }
}

function buildTokenScopePlan() {
    const light = new Map(builtinThemes.vs.rules.map(rule => [rule.token, rule]));
    const dark = new Map(builtinThemes.vs_dark.rules.map(rule => [rule.token, rule]));
    const scopes = [...new Set([...light.keys(), ...dark.keys()])].filter(scope => scope && scope !== "keyword.md").sort();
    return scopes.map(scope => {
        const pair = [resolveBuiltinRule(light, scope, "foreground"), resolveBuiltinRule(dark, scope, "foreground")];
        return { scope, pair, key: pair.join("|"), fontStyle: resolveBuiltinRule(light, scope, "fontStyle") };
    });
}

function nudgeColor(hex, used) {
    const value = parseInt(hex, 16);
    for (let step = 0; ; step++) {
        const delta = step === 0 ? 0 : (step % 2 ? 1 : -1) * Math.ceil(step / 2);
        const blue = (value & 255) + delta;
        if (blue < 0 || blue > 255) continue;
        const candidate = ((value & 0xFFFF00) | blue).toString(16).padStart(6, "0").toUpperCase();
        if (!used.has(candidate)) { used.add(candidate); return candidate; }
    }
}

function mergedTokenRules(dark, foreground, background) {
    tokenScopePlan ??= buildTokenScopePlan();
    const side = dark ? 1 : 0;
    const defaultColor = foreground.slice(1, 7).toUpperCase();
    const used = new Set([defaultColor, background.slice(1, 7).toUpperCase()]);
    const pairColors = new Map();
    const rules = [{ token: "keyword.md", foreground: defaultColor, fontStyle: "bold" }];
    for (const { scope, pair, key, fontStyle } of tokenScopePlan) {
        const rule = { token: scope };
        if (pair[0] !== null || pair[1] !== null) {
            if (!pairColors.has(key)) pairColors.set(key, nudgeColor((pair[side] ?? defaultColor).toUpperCase(), used));
            rule.foreground = pairColors.get(key);
        }
        if (fontStyle !== null) rule.fontStyle = fontStyle;
        rules.push(rule);
    }
    return rules;
}

const paletteColorNames = new Set();
let themeColorTimer = null;
let themePaletteTransitions = [];
let themePaletteTarget = null;
let themeSyntaxBridge = null;
let themeColorStart = 0;
let preparedThemeMotion = null;

function registerPaletteColors(palette) {
    for (const [name, value] of Object.entries(palette)) {
        if (typeof value !== "string" || paletteColorNames.has(name)) continue;
        CSS.registerProperty({ name: "--" + name, syntax: "<color>", inherits: true, initialValue: "transparent" });
        paletteColorNames.add(name);
    }
}

function roundThemeByte(value) {
    const bounded = Math.max(0, Math.min(255, value));
    const lower = Math.floor(bounded);
    return bounded - lower === .5 ? lower + lower % 2 : Math.round(bounded);
}

function parseThemeColor(value) {
    if (typeof value !== "string") return null;
    const text = value.trim().toLowerCase();
    if (text === "transparent") return [0, 0, 0, 0];
    const hex = /^#([\da-f]{3,4}|[\da-f]{6}|[\da-f]{8})$/.exec(text);
    if (hex) {
        const full = hex[1].length <= 4 ? [...hex[1]].map(character => character + character).join("") : hex[1];
        return [0, 2, 4].map(index => parseInt(full.slice(index, index + 2), 16))
            .concat(full.length === 8 ? parseInt(full.slice(6, 8), 16) : 255);
    }
    const rgb = /^rgba?\((.+)\)$/.exec(text);
    const srgb = /^color\(srgb\s+(.+)\)$/.exec(text);
    if (!rgb && !srgb) return null;
    const components = (rgb || srgb)[1].trim().split(/[\s,/]+/);
    if (components.length < 3 || components.length > 4 || components.some(component =>
        !/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?%?$/.test(component) || !Number.isFinite(parseFloat(component)))) return null;
    const channel = (component, scale) => roundThemeByte(component.endsWith("%")
        ? parseFloat(component) / 100 * 255 : parseFloat(component) * scale);
    return components.slice(0, 3).map(component => channel(component, srgb ? 255 : 1))
        .concat(components.length === 4 ? channel(components[3], 255) : 255);
}

function interpolateThemeColor(from, to, amount) {
    if (amount <= 0) return [...from];
    if (amount >= 1) return [...to];
    const alpha = from[3] + (to[3] - from[3]) * amount;
    return [0, 1, 2].map(channel => alpha <= 0 ? to[channel] : roundThemeByte(
        (from[channel] * from[3] * (1 - amount) + to[channel] * to[3] * amount) / alpha))
        .concat(roundThemeByte(alpha));
}

function captureThemePalette(palette) {
    const computed = getComputedStyle(document.documentElement);
    const transitions = [];
    for (const [name, targetValue] of Object.entries(palette)) {
        if (typeof targetValue !== "string") continue;
        const from = parseThemeColor(computed.getPropertyValue("--" + name));
        const to = parseThemeColor(targetValue);
        if (!from || !to) return null;
        transitions.push({ name, from, to, targetValue });
    }
    return transitions;
}

function renderThemePalette(amount) {
    const root = document.documentElement;
    for (const transition of themePaletteTransitions) {
        const [r, g, b, a] = interpolateThemeColor(transition.from, transition.to, amount);
        root.style.setProperty("--" + transition.name, `rgba(${r}, ${g}, ${b}, ${a / 255})`);
    }
}

function readMonacoSyntaxColors() {
    const colors = new Map();
    // These are public stylesheet rules emitted by the local Monaco themes, not private editor services.
    for (const style of document.querySelectorAll("style.monaco-colors")) {
        for (const rule of style.sheet?.cssRules || []) {
            const match = /^\.(mtk\d+)$/.exec(rule.selectorText || "");
            if (!match) continue;
            const color = parseThemeColor(rule.style.color);
            if (color) colors.set(match[1], color);
        }
    }
    return colors;
}

function captureThemeSyntaxColors() {
    const colors = readMonacoSyntaxColors();
    if (themeSyntaxBridge) {
        for (const [name, color] of themeSyntaxBridge.currentColors) colors.set(name, [...color]);
    }
    const foreground = parseThemeColor(getComputedStyle(document.documentElement).getPropertyValue("--foreground"));
    if (foreground) colors.set("mtk1", foreground);
    return colors;
}

function setSyntaxRuleColor(rule, color) {
    const [r, g, b, a] = color;
    rule.style.color = `rgba(${r}, ${g}, ${b}, ${a / 255})`;
}

function ensureThemeSyntaxRule(name, color) {
    let rule = themeSyntaxBridge.rules.get(name);
    if (!rule) {
        const sheet = themeSyntaxBridge.style.sheet;
        const index = sheet.insertRule(`.${name}:where(:not(.changed-old):not(.changed-new)) {}`, sheet.cssRules.length);
        rule = sheet.cssRules[index];
        themeSyntaxBridge.rules.set(name, rule);
    }
    // Keep Monaco's original selector priority. Bracket and business decorations stay stronger.
    setSyntaxRuleColor(rule, color);
    themeSyntaxBridge.currentColors.set(name, [...color]);
    return rule;
}

function prepareThemeSyntaxBridge(sourceColors) {
    if (!themeSyntaxBridge) {
        const style = document.createElement("style");
        style.className = "differ-theme-token-colors";
        document.head.appendChild(style);
        themeSyntaxBridge = { style, currentColors: new Map(), rules: new Map(), transitions: [] };
    }
    themeSyntaxBridge.transitions = [];
    for (const [name, color] of sourceColors) ensureThemeSyntaxRule(name, color);
}

function setThemeSyntaxTarget(sourceColors) {
    if (!themeSyntaxBridge) return;
    for (const [name, to] of readMonacoSyntaxColors()) {
        const from = sourceColors.get(name) || to;
        const rule = ensureThemeSyntaxRule(name, from);
        themeSyntaxBridge.transitions.push({ name, from: [...from], to: [...to], rule });
    }
}

function renderThemeSyntax(amount) {
    if (!themeSyntaxBridge) return;
    for (const transition of themeSyntaxBridge.transitions) {
        const color = interpolateThemeColor(transition.from, transition.to, amount);
        setSyntaxRuleColor(transition.rule, color);
        themeSyntaxBridge.currentColors.set(transition.name, color);
    }
}

function clearThemeSyntaxBridge() {
    themeSyntaxBridge?.style.remove();
    themeSyntaxBridge = null;
}

function commitThemePaletteTarget() {
    if (!themePaletteTarget) return;
    for (const [name, value] of Object.entries(themePaletteTarget)) {
        if (typeof value === "string") document.documentElement.style.setProperty("--" + name, value);
    }
    appliedPalette = { ...appliedPalette, ...themePaletteTarget };
    themePaletteTarget = null;
}

function finishThemeColors(complete = true) {
    preparedThemeMotion = null;
    if (themeColorTimer === null) {
        if (complete) { commitThemePaletteTarget(); clearThemeSyntaxBridge(); }
        return;
    }
    clearTimeout(themeColorTimer);
    themeColorTimer = null;
    const root = document.documentElement;
    const computed = getComputedStyle(root);
    const current = complete ? null : [...paletteColorNames].map(name => [name, computed.getPropertyValue("--" + name)]);
    root.style.transitionProperty = "none";
    document.body.classList.remove("theme-color-transition");
    if (complete) {
        commitThemePaletteTarget();
        renderThemeSyntax(1);
        clearThemeSyntaxBridge();
    }
    if (current) {
        for (const [name, value] of current) root.style.setProperty("--" + name, value);
        appliedPalette = { ...appliedPalette, ...Object.fromEntries(current) };
    }
    themePaletteTransitions = [];
    scheduleDifferenceRailRepaint();
}

function applyThemeTransition(message) {
    const prepared = message.mist?.waitForStart === true;
    const elapsed = prepared ? 0 : message.mist ? performance.timeOrigin + performance.now() - message.mist.startedAt : 680;
    let animated = message.palette && message.mist && elapsed < 680 && !document.hidden &&
        !reduceThemeMotion.matches && !forceThemeColors.matches;
    finishThemeColors(!animated);
    const syntaxSource = animated ? captureThemeSyntaxColors() : null;
    if (animated) {
        registerPaletteColors(message.palette);
        const transitions = captureThemePalette(message.palette);
        if (transitions) {
            themePaletteTransitions = transitions;
            themePaletteTarget = { ...message.palette };
        }
        else animated = false;
    }
    if (!animated) { themePaletteTransitions = []; themePaletteTarget = null; }
    if (animated) {
        const root = document.documentElement;
        prepareThemeSyntaxBridge(syntaxSource);
        // Palette and syntax classes commit together, without seeking animations on replaceable token nodes.
        root.style.transitionProperty = "none";
        document.body.classList.add("theme-color-transition");
        themeColorTimer = prepared ? -1 : setTimeout(() => finishThemeColors(), Math.max(0, 680 - elapsed));
    }
    setTheme(message);
    if (animated) {
        setThemeSyntaxTarget(syntaxSource);
        themeColorStart = prepared ? Infinity : message.mist.startedAt - performance.timeOrigin;
        renderThemeColors(performance.now());
    }
    else clearThemeSyntaxBridge();
    if (animated && prepared) {
        stopThemeMist();
        preparedThemeMotion = { ...message.mist, dark: message.dark };
        const id = message.mist.transitionId;
        send({ type: "themeTransitionReady", id });
        return;
    }
    if (prepared && !animated) {
        // An unavailable animation must settle both prepared surfaces instead of leaving the host waiting.
        stopThemeMist();
        const id = message.mist.transitionId;
        send({ type: "themeTransitionReady", id });
        send({ type: "themeTransitionFrame", id, elapsed: themeMistDuration });
        return;
    }
    startThemeMist(message.mist, message.dark);
}

function startPreparedTheme(message) {
    if (!preparedThemeMotion || preparedThemeMotion.transitionId !== message.id) return;
    const prepared = preparedThemeMotion;
    preparedThemeMotion = null;
    themeColorStart = message.startedAt - performance.timeOrigin;
    const remaining = Math.max(0, themeMistDuration - (performance.now() - themeColorStart));
    themeColorTimer = setTimeout(finishThemeMotion, remaining);
    startThemeMist({ ...prepared, startedAt: message.startedAt }, prepared.dark);
}

function renderThemeColors(now) {
    if (themeColorTimer === null) return;
    const progress = Math.max(0, Math.min(1, (now - themeColorStart) / themeMistDuration));
    const amount = progress < .5 ? 4 * progress ** 3 : 1 - (2 - 2 * progress) ** 3 / 2;
    renderThemePalette(amount);
    renderThemeSyntax(amount);
}

const themeMistCanvas = document.getElementById("theme-mist");
const reduceThemeMotion = matchMedia("(prefers-reduced-motion: reduce)");
const forceThemeColors = matchMedia("(forced-colors: active)");
const themeMistDuration = 680;
let themeMistFrame = null;
let activeThemeTransitionId = null;

// These stops, radii and timings match the WPF fog so the two surfaces meet at the WebView edge.
// The selected HTML scheme 2 uses one soft radial light field, with final alpha matching XAML.
const morningMist = [[0, "255 255 255", 80 / 255], [.40, "251 191 36", 57 / 255], [1, "0 0 0", 0]];
const moonMist = [[0, "186 230 253", 57 / 255], [.35, "56 189 248", 36 / 255],
    [.70, "30 58 138", 21 / 255], [1, "0 0 0", 0]];

function stopThemeMist() {
    if (themeMistFrame !== null) cancelAnimationFrame(themeMistFrame);
    themeMistFrame = null;
    activeThemeTransitionId = null;
    themeMistCanvas.hidden = true;
}

function createThemeMistGradient(context, x, y, radius, stops, opacity) {
    const gradient = context.createRadialGradient(x, y, 0, x, y, radius);
    for (const [offset, rgb, alpha] of stops) {
        gradient.addColorStop(offset, `rgb(${rgb} / ${alpha * opacity})`);
    }
    return gradient;
}

function startThemeMist(mist, dark) {
    stopThemeMist();
    if (!mist || document.hidden || reduceThemeMotion.matches || forceThemeColors.matches) return;
    const { x, y, width, height, startedAt } = mist;
    if (![x, y, width, height, startedAt].every(Number.isFinite) || width <= 0 || height <= 0) return;

    const start = startedAt - performance.timeOrigin;
    if (Math.abs(performance.now() - start) >= themeMistDuration) return;
    const viewWidth = window.innerWidth;
    const viewHeight = window.innerHeight;
    if (!viewWidth || !viewHeight) return;
    const context = themeMistCanvas.getContext("2d");
    if (!context) return;

    const pixelWidth = Math.ceil(viewWidth / 2);
    const pixelHeight = Math.ceil(viewHeight / 2);
    if (themeMistCanvas.width !== pixelWidth) themeMistCanvas.width = pixelWidth;
    if (themeMistCanvas.height !== pixelHeight) themeMistCanvas.height = pixelHeight;
    context.setTransform(pixelWidth / viewWidth, 0, 0, pixelHeight / viewHeight, 0, 0);
    context.clearRect(0, 0, viewWidth, viewHeight);
    themeMistCanvas.hidden = false;

    const originX = x * viewWidth;
    const originY = y * viewHeight;
    const distance = Math.hypot(width * viewWidth, height * viewHeight) * 1.35;
    const palette = dark ? moonMist : morningMist;
    activeThemeTransitionId = mist.transitionId ?? null;

    function render(now) {
        const progress = Math.max(0, Math.min(1, (now - start) / themeMistDuration));
        if (progress === 1) {
            finishThemeMotion();
            return;
        }

        const eased = progress < .5 ? 4 * progress ** 3 : 1 - (-2 * progress + 2) ** 3 / 2;
        const radius = distance * (dark ? 1 - eased : eased);
        const envelope = Math.sin(progress ** .88 * Math.PI);
        const mainRadius = Math.max(20, radius);

        context.clearRect(0, 0, viewWidth, viewHeight);
        context.fillStyle = createThemeMistGradient(context, originX, originY, mainRadius, palette, envelope);
        context.fillRect(0, 0, viewWidth, viewHeight);

        if (themeColorTimer !== null) scheduleDifferenceRailRepaint();

        renderThemeColors(now);
        if (activeThemeTransitionId !== null)
            send({ type: "themeTransitionFrame", id: activeThemeTransitionId, elapsed: Math.max(0, now - start) });
        themeMistFrame = requestAnimationFrame(() => render(performance.now()));
    }

    // Paint the target-theme fog in this message task, before the new palette is presented.
    render(performance.now());
}

function finishThemeMotion() {
    const id = activeThemeTransitionId ?? preparedThemeMotion?.transitionId ?? null;
    stopThemeMist();
    finishThemeColors();
    if (id !== null) send({ type: "themeTransitionFrame", id, elapsed: themeMistDuration });
}
window.addEventListener("resize", finishThemeMotion);
window.visualViewport?.addEventListener("resize", finishThemeMotion);
document.addEventListener("visibilitychange", () => { if (document.hidden) finishThemeMotion(); });
reduceThemeMotion.addEventListener("change", finishThemeMotion);
forceThemeColors.addEventListener("change", finishThemeMotion);
