"use strict";
// Monaco owns document/undo/input only. All differences and their versions come from the host.
let left, right, diff, original, modified, session, version, projectionVersion=0;
let editMode=false,initialHistoryVersion=0,latestHistoryVersion=0,canUndo=false,canRedo=false,lastStatus="",statusFrame=0;
let currentContext="",approximateHighlight=false;
let lineChanges=[], changedRows=[], decorations=[[],[]], inlineDecorations=[[],[]], focusDecorations=[[],[]];
let inlineRows=[],zoneRecords=[[],[]],lastPaintedVersion=0,modifiedZoneAnchorsDirty=false;
let zones=[[],[]], hasPendingText=false, pendingText, pendingInline, lastEditAt=0, syncing=false;
let activeHunk=-1,navigationSequence=0,feedbackFrame=0,feedbackFrames=[],feedbackRails=[],zoneNodes=[new Map(),new Map()];
let navigationLabels={currentBlock:"Block {0} / {1} · {2} · BASE {3} ↔ Result {4}",line:"line {0}",lines:"lines {0}–{1}",missingAtStart:"no lines at file start",missingAfter:"no lines after {0}",notSelected:"Select a block with F7 / F8",noDifferences:"No differences",updating:"Updating differences",added:"Added",deleted:"Deleted",modified:"Modified",unchangedLocation:"{0} · No difference here",resultSide:"Result"};
let pendingFold;
let foldRanges=[[],[]], foldListeners=[], foldSync=false, displayMode=0,foldSignature="",foldModePending=true;
const send=value=>window.chrome.webview.postMessage(value);
require.config({paths:{vs:"vs"}});
require(["vs/editor/editor.main"],()=>{
 const settings={automaticLayout:true,readOnly:true,domReadOnly:true,minimap:{enabled:false},fontSize:13.5,lineHeight:24,
  scrollBeyondLastLine:false,smoothScrolling:false,wordWrap:"off",folding:true,renderLineHighlight:"none",
  unicodeHighlight:{ambiguousCharacters:false,nonBasicASCII:false},bracketPairColorization:{enabled:true},
  guides:{indentation:true},stickyScroll:{enabled:false},scrollbar:{alwaysConsumeMouseWheel:false}};
 left=monaco.editor.create(document.getElementById("base"),{...settings,readOnly:true});
 right=monaco.editor.create(document.getElementById("mine"),settings);
 for(const [side,editor] of [[0,left],[1,right]]){
  const frame=document.createElement("div");frame.className="current-hunk-frame";frame.hidden=true;frame.setAttribute("aria-hidden","true");
  editor.getDomNode().parentElement.appendChild(frame);feedbackFrames.push(frame);
  const rail=document.createElement("div");rail.className="current-hunk-rail";rail.hidden=true;rail.setAttribute("aria-hidden","true");
  editor.getDomNode().parentElement.appendChild(rail);feedbackRails.push(rail);
  editor.onDidLayoutChange(scheduleNavigationFeedback);
 }
 // This convenience object exposes public standalone-editor APIs to the Windows regression probe.
 diff={getModifiedEditor:()=>right,getOriginalEditor:()=>left};
 const languages=["lua","csharp","javascript","typescript","json","xml","python","cpp","sql","html","css","markdown","yaml","plaintext"];
 for(const language of languages)monaco.languages.registerFoldingRangeProvider(language,{
  onDidChange:listener=>{foldListeners.push(listener);return{dispose:()=>{foldListeners=foldListeners.filter(x=>x!==listener);}};},
  provideFoldingRanges:model=>model===original?foldRanges[0]:model===modified?foldRanges[1]:[]
 });
 for(const [editor,other] of [[left,right],[right,left]]){
  editor.onDidScrollChange(()=>{
   if(!syncing){syncing=true;other.setScrollPosition({scrollTop:editor.getScrollTop(),scrollLeft:editor.getScrollLeft()});syncing=false;}
   requestInline();scheduleNavigationFeedback();
   if(modified){const r=right.getVisibleRanges();if(r.length)send({type:"viewport",start:(r[0].startLineNumber-1)/modified.getLineCount(),end:r[r.length-1].endLineNumber/modified.getLineCount()});}
  });
  editor.onDidChangeHiddenAreas(()=>{
   scheduleNavigationFeedback();if(foldSync)return;
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
 document.getElementById("adopt-current").addEventListener("click",adoptCurrent);
 send({type:"ready"});
});
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
 for(const [i,editor] of [[0,left],[1,right]]){decorations[i]=editor.deltaDecorations(decorations[i],[]);inlineDecorations[i]=editor.deltaDecorations(inlineDecorations[i],[]);editor.changeViewZones(accessor=>{for(const id of zones[i])accessor.removeZone(id);zones[i]=[];});}
 clearNavigationFeedback();zoneNodes=[new Map(),new Map()];zoneRecords=[[],[]];inlineRows=[];lastPaintedVersion=0;foldSignature="";modifiedZoneAnchorsDirty=false;
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
 syncing=false;
 setFoldingRegions();
 renderCurrentLocation();requestInline();
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
 publishStatus();
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
 document.getElementById("adopt-current").hidden=true;
 renderCurrentLocation();
}
function scheduleNavigationFeedback(){if(!feedbackFrame)feedbackFrame=requestAnimationFrame(()=>{feedbackFrame=0;positionNavigationFeedback();});}
function positionNavigationFeedback(){
 const adopt=document.getElementById("adopt-current");adopt.hidden=true;adopt.disabled=!editMode;
 if(activeHunk<0||projectionVersion!==version||hasPendingText){for(const frame of [...feedbackFrames,...feedbackRails])frame.hidden=true;return;}
 const change=lineChanges[activeHunk];
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
  if(!frame.hidden&&adopt.hidden){adopt.hidden=false;adopt.style.top=`${Math.min(Math.max(0,visibleTop),Math.max(0,layout.height-24))}px`;adopt.dataset.block=String(activeHunk+1);}
 }
}
function flashNavigationFeedback(){
 for(const frame of feedbackFrames){frame.classList.remove("locating");void frame.offsetWidth;frame.classList.add("locating");}
}
function revealPaired(editor,line,end=line,sequence=++navigationSequence){
 clearTimeout(pendingFold);const requestedVersion=version;foldSync=true;
 Promise.all([left.getAction("editor.unfoldAll").run(),right.getAction("editor.unfoldAll").run()]).then(()=>{
  if(sequence!==navigationSequence||requestedVersion!==version||hasPendingText)return;
  editor.revealLinesInCenter(Math.max(1,line),Math.max(1,end),monaco.editor.ScrollType.Immediate);
  requestAnimationFrame(()=>{if(sequence!==navigationSequence)return;positionNavigationFeedback();flashNavigationFeedback();});
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
function revealLocation(mine,line){
 if(projectionVersion!==version||hasPendingText)return; const side=mine?1:0,index=lineChanges.findIndex(change=>{const range=sideRange(change,side);return range.end>0&&line>=range.start&&line<=range.end;});
 if(index>=0)selectHunk(index,false);
 else{
  clearNavigationFeedback();send({type:"selected",session,version,index:-1});
  document.getElementById("current-location").textContent=formatLabel(navigationLabels.unchangedLocation,`${mine?navigationLabels.resultSide:"BASE"} ${formatLabel(navigationLabels.line,line)}`);
  publishStatus();
 }
 revealPaired(mine?right:left,line);
}
window.chrome.webview.addEventListener("message",event=>{
 const m=event.data;if(!right)return;
 switch(m.type){
 case "load":
  clearTimeout(pendingText);hasPendingText=false;clearProjection();if(original)original.dispose();if(modified)modified.dispose();
  navigationLabels=m.navigationLabels||navigationLabels;session=m.session;version=m.version;projectionVersion=0;lineChanges=[];foldModePending=true;editMode=!!m.editMode;canUndo=canRedo=false;lastStatus="";currentContext="";approximateHighlight=false;
  document.getElementById("adopt-current").title=navigationLabels.adoptCurrent||"Use left block";
  document.getElementById("adopt-current").setAttribute("aria-label",navigationLabels.adoptCurrent||"Use left block");
  original=monaco.editor.createModel(m.original,m.language);modified=monaco.editor.createModel(m.modified,m.language);
  original.setEOL(monaco.editor.EndOfLineSequence.LF);modified.setEOL(monaco.editor.EndOfLineSequence.LF);
  left.setModel(original);right.setModel(modified);left.updateOptions({readOnlyMessage:{value:navigationLabels.readOnlyBase}});right.updateOptions({readOnly:!editMode,domReadOnly:!editMode,readOnlyMessage:{value:navigationLabels.readOnlyMine}});setTheme(m);
  initialHistoryVersion=latestHistoryVersion=modified.getAlternativeVersionId();
  modified.onDidChangeContent(e=>{
   if(e.changes.some(change=>change.range.startLineNumber!==change.range.endLineNumber||/[\r\n]/.test(change.text)))modifiedZoneAnchorsDirty=true;
   const historyVersion=modified.getAlternativeVersionId();canUndo=historyVersion!==initialHistoryVersion;
   if(e.isUndoing)canRedo=true;else if(e.isRedoing)canRedo=historyVersion!==latestHistoryVersion;else{canRedo=false;latestHistoryVersion=historyVersion;}
   lastEditAt=performance.now();projectionVersion=0;lineChanges=[];approximateHighlight=false;pruneInlineRows();clearNavigationFeedback();if(!hasPendingText){hasPendingText=true;send({type:"pending",session});renderCurrentLocation();}clearTimeout(pendingText);pendingText=setTimeout(flushText,100);publishStatus();requestAnimationFrame(()=>send({type:"metric",operation:"editor-input-frame",elapsedMs:performance.now()-lastEditAt}));
  });updateContext(1);renderCurrentLocation();break;
 case "editMode":
  if(m.session!==session)break;
  // One renderer turn freezes input before sending its final document batch and acknowledgement.
  editMode=!!m.enabled;right.updateOptions({readOnly:!editMode,domReadOnly:!editMode});flushText();
  positionNavigationFeedback();publishStatus();send({type:"modeChanged",id:m.id,session,version});break;
 case "clear":
  editMode=false;right.updateOptions({readOnly:true,domReadOnly:true});clearTimeout(pendingText);hasPendingText=false;clearProjection();left.setModel(null);right.setModel(null);original?.dispose();modified?.dispose();original=modified=null;session=null;break;
 case "projection":applyProjection(m);break;
 case "projectionFailed":
  if(m.session!==session||m.version!==version||hasPendingText)break;
  projectionVersion=0;lineChanges=[];changedRows=[];clearProjection();foldRanges=[[],[]];for(const listener of foldListeners)listener();
  foldSync=true;Promise.all([left.getAction("editor.unfoldAll").run(),right.getAction("editor.unfoldAll").run()]).finally(()=>foldSync=false);break;
 case "inline":applyInline(m);break;
 case "theme":setTheme(m);break;
 case "revealHunk":if(m.version===version)selectHunk(m.index,true);break;
 case "reveal":if(m.version===version)revealLocation(m.mine,m.line);break;
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
  const p=m.palette;
  monaco.editor.defineTheme("differ",{base:m.dark?"vs-dark":"vs",inherit:true,rules:[],colors:{
   "editor.background":p.background,"editor.foreground":p.foreground,"editorGutter.background":p.background,
   "editorLineNumber.foreground":p.lineNumber,"editorLineNumber.activeForeground":p.focus,
   "editorCursor.foreground":p.focus,"editor.selectionBackground":p.selection,"editor.inactiveSelectionBackground":p.inactiveSelection,
   "editorIndentGuide.background1":p.indent,"editorIndentGuide.activeBackground1":p.border,
   "scrollbarSlider.background":p.scrollThumb,"scrollbarSlider.hoverBackground":p.scrollHover,"scrollbarSlider.activeBackground":p.scrollPressed,
   "editorWidget.background":p.chrome,"editorWidget.border":p.border,"editorWidget.foreground":p.foreground,"focusBorder":p.focus,
   "input.background":p.background,"input.foreground":p.foreground,"input.border":p.border
  }});
  monaco.editor.setTheme("differ");
  for(const[name,value]of Object.entries(p))if(typeof value==="string")document.documentElement.style.setProperty("--"+name,value);
  if(p.fontSize)for(const [side,editor]of [[0,left],[1,right]]){
   const lineHeight=p.lineHeight||editor.getOption(monaco.editor.EditorOption.lineHeight);
   const resized=lineHeight!==editor.getOption(monaco.editor.EditorOption.lineHeight);
   editor.updateOptions({fontSize:p.fontSize,lineHeight});
   // Spacers use line counts, so zoom remeasures existing nodes without replacing them.
   if(resized)editor.changeViewZones(accessor=>{for(const record of zoneRecords[side])accessor.layoutZone(record.id);});
  }
  scheduleNavigationFeedback();
 }else monaco.editor.setTheme(m.dark?"vs-dark":"vs");
 document.body.classList.toggle("dark",m.dark);
}
