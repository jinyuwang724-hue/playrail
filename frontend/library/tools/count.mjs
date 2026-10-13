const W=12,H=8;
const S=[[2,2],[2,3],[3,2],[3,3],[2,4],[4,2],[3,4],[4,3],[4,4],[6,2],[2,6]];
function count(fixed){ // fixed rect [x,y,w,h]
  const g=new Uint8Array(W*H); const [fx,fy,fw,fh]=fixed;
  for(let y=fy;y<fy+fh;y++)for(let x=fx;x<fx+fw;x++)g[y*W+x]=1;
  let n=0;
  function dfs(placed){
    let i=g.indexOf(0); if(i<0){ if(placed===11)n++; return;}
    if(placed>=11)return;
    const x=i%W,y=(i/W)|0;
    for(const [w,h] of S){ if(x+w>W||y+h>H)continue; let ok=true;
      for(let yy=y;yy<y+h&&ok;yy++)for(let xx=x;xx<x+w;xx++)if(g[yy*W+xx]){ok=false;break;}
      if(!ok)continue;
      for(let yy=y;yy<y+h;yy++)for(let xx=x;xx<x+w;xx++)g[yy*W+xx]=1;
      dfs(placed+1);
      for(let yy=y;yy<y+h;yy++)for(let xx=x;xx<x+w;xx++)g[yy*W+xx]=0;
    }
  }
  dfs(0); return n;
}
for(const p of [[0,0],[3,1],[6,2],[3,0]]){console.time(p+'');console.log(p,count([p[0],p[1],6,6]));console.timeEnd(p+'');}
