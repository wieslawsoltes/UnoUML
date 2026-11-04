// Inclusive calendar-day intervals. All pointer and keyboard paths use the same
// bounded arithmetic, leaving dependency validation to the C# schedule model.
export function ganttRange(start,duration,delta,mode='move',milestone=false) {
  if(![start,duration,delta].every(Number.isInteger)||start<0||duration<1||start+duration>1096)
    throw new RangeError('Invalid timeline interval.');
  if(milestone)duration=1;
  if(mode==='start'&&!milestone) {
    const end=start+duration;
    start=Math.max(0,Math.min(end-1,start+delta));duration=end-start;
  } else if(mode==='end'&&!milestone) duration=Math.max(1,Math.min(1096-start,duration+delta));
  else start=Math.max(0,Math.min(1096-duration,start+delta));
  return {start,duration};
}
