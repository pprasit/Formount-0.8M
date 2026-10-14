let canvas,context,raw,output,pending,busy=false;
onmessage=event=>{
  const data=event.data;
  if(data.raw){
    canvas=new OffscreenCanvas(data.width,data.height);context=canvas.getContext("2d");raw=new Uint16Array(data.raw);output=context.createImageData(data.width,data.height);
  }else if(data.bitmap){
    canvas=new OffscreenCanvas(data.bitmap.width,data.bitmap.height);
    context=canvas.getContext('2d',{willReadFrequently:true});
    context.drawImage(data.bitmap,0,0);data.bitmap.close();
    const packed=context.getImageData(0,0,canvas.width,canvas.height).data;
    raw=new Uint16Array(canvas.width*canvas.height);
    for(let i=0;i<raw.length;i++)raw[i]=(packed[i*4]<<8)|packed[i*4+1];
    output=context.createImageData(canvas.width,canvas.height);
  }else{pending=data;render();}
};
async function render(){
  if(busy||!raw)return;busy=true;
  while(pending){
    const {view,version}=pending;pending=null;
    const lut=new Uint8Array(65536),span=Math.max(1,view.white-view.black),tone=view.tone||5,normal=Math.asinh(tone);
    for(let i=0;i<65536;i++)lut[i]=Math.round(255*Math.asinh(Math.min(1,Math.max(0,(i-view.black)/span))*tone)/normal);
    for(let i=0;i<raw.length;i++){const value=lut[raw[i]],j=i*4;output.data[j]=output.data[j+1]=output.data[j+2]=value;output.data[j+3]=255;}
    context.putImageData(output,0,0);
    const blob=await canvas.convertToBlob({type:'image/webp',quality:.9});
    // Show completed frames while dragging; pending input supplies the next frame.
    postMessage({blob,version,width:canvas.width,height:canvas.height});
    await new Promise(resolve=>setTimeout(resolve,12));
  }
  busy=false;
}
