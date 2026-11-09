import './styles.css';
import { attachInput } from './input';
import { Stage } from './stage/stage';

const root = document.getElementById('app')!;
const stage = new Stage(root);

// 性能读数（视图键 / F 切换）：最近 1 秒的平均与最长帧时间、卡片数
const hud = document.createElement('div');
hud.className = 'hud';
document.body.appendChild(hud);
let hudOn = new URLSearchParams(location.search).has('hud');
hud.hidden = !hudOn;
const frames: number[] = [];
const work: number[] = [];
let lastT = 0;
const sample = (t: number) => {
    if (lastT) frames.push(t - lastT);
    lastT = t;
    work.push(stage.lastWorkMs);
    if (frames.length > 60) frames.shift();
    if (work.length > 60) work.shift();
    if (hudOn && frames.length) {
        const avg = frames.reduce((a, b) => a + b, 0) / frames.length;
        const max = Math.max(...frames);
        const js = Math.max(...work);
        hud.textContent = `帧 ${avg.toFixed(1)} ms · 最长 ${max.toFixed(1)} ms · ${Math.round(1000 / avg)} fps · 脚本最长 ${js.toFixed(1)} ms · 卡片 ${stage.tileCount}`;
    }
    if (hudOn) requestAnimationFrame(sample);
    else lastT = 0;
};
const toggleHud = () => {
    hudOn = !hudOn;
    hud.hidden = !hudOn;
    frames.length = 0;
    if (hudOn) requestAnimationFrame(sample);
};
if (hudOn) requestAnimationFrame(sample);

attachInput(stage, toggleHud);
stage.start();
(window as unknown as { stage: Stage }).stage = stage;
