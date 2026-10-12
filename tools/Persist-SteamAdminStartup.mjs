import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
const target=path.join(process.env.APPDATA,'GamingMode','config.json');
const backup=process.argv[2];
assert(backup,'Backup directory is required');
const bytes=fs.readFileSync(target);
const config=JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/,''));
const launcher=path.join(process.env.USERPROFILE,'homebrew','plugins','claw-button-router','bin','SteamAdminLauncher.exe');
assert(fs.existsSync(launcher));
assert(Array.isArray(config.gaming?.customStartupApps));
const name='Steam Administrator Launcher';
config.gaming.customStartupApps=config.gaming.customStartupApps.filter(a=>a.name!==name && a.path?.toLowerCase()!==launcher.toLowerCase());
config.gaming.customStartupApps.unshift({name,path:launcher,arguments:'',workingDirectory:path.dirname(launcher),processName:'steam',enabled:true,startMinimized:true,delayAfterStartMs:1500});
fs.mkdirSync(backup,{recursive:true});
fs.writeFileSync(path.join(backup,'GamingMode-config.json'),bytes,{flag:'wx'});
assert(fs.readFileSync(target).equals(bytes),'Config changed during preparation');
const temporary=target+`.steam-admin-${process.pid}.tmp`;
try {
 fs.writeFileSync(temporary,JSON.stringify(config,null,2)+'\n',{flag:'wx'});
 assert(fs.readFileSync(target).equals(bytes),'Config changed before installation');
 fs.renameSync(temporary,target);
} finally { if(fs.existsSync(temporary)) fs.unlinkSync(temporary); }
console.log(JSON.stringify({ok:true,firstStartupApp:name,backup}));
