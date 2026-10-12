// MSI Claw: keep Steam downloads and the installed Chinese games on DIRECT.
// Global extension: reapplied by Clash Verge after subscription updates.
function main(config, profileName) {
  const steamDownloads = [
    'steamcontent.com',
    'steampipe.akamaized.net',
    'steampipe-partner.akamaized.net',
    'steampipe-kr.akamaized.net',
    'dl.steam.clngaa.com',
    'dl.steam.ksyna.com',
    'st.dl.bscstorage.net',
    'st.dl.eccdnx.com',
    'st.dl.pinyuncloud.com',
    'steampipe.steamcontent.tnkjmec.com',
    'steampowered.com.8686c.com',
    'steamstatic.com.8686c.com',
    'gstore.val.manlaxy.com',
    'xz.pphimalayanrt.com',
    'lv.queniujq.cn',
    'client-update.queniuqe.com',
    'wmsjsteam.com',
    'steamchina.com',
    'steam.cdn.on.net',
    'steam.apac.qtlglb.com',
    'steam.eca.qtlglb.com',
    'steam.naeu.qtlglb.com',
    'steam.ru.qtlglb.com',
    'steam.cdn.webra.ru',
    'edge.steam-dns.top.comcast.net',
    'steam.cdn.orcon.net.nz',
    'steam.cdn.slingshot.co.nz',
  ];
  const chineseGameDomains = [
    'mihoyo.com', 'miyoushe.com', 'yuanshen.com',
    'kurogame.com', 'kurogame.net', 'kurogames.com', 'kurocdn.com', 'kurobbs.com',
  ];
  const rules = [
    'PROCESS-PATH,C:\\Game\\ZenlessZoneZero Game\\ZenlessZoneZero.exe,DIRECT',
    'PROCESS-PATH,C:\\Game\\Wuthering Waves Game\\Client\\Binaries\\Win64\\Client-Win64-Shipping.exe,DIRECT',
  ];
  const domains = steamDownloads.concat(chineseGameDomains);
  domains.forEach(function (domain) { rules.push('DOMAIN-SUFFIX,' + domain + ',DIRECT'); });
  rules.push('DOMAIN-SUFFIX,cn,DIRECT');
  config.rules = rules.concat((config.rules || []).filter(function (rule) {
    return rules.indexOf(rule) === -1;
  }));

  // Retain the normal on-demand process lookup; do not force a process scan for every connection.
  config['find-process-mode'] = 'strict';
  const dns = config.dns || {};
  const domesticDns = ['https://dns.alidns.com/dns-query', 'https://doh.pub/dns-query'];
  const policy = dns['nameserver-policy'] || {};
  domains.concat(['cn']).forEach(function (domain) { policy['+.' + domain] = domesticDns.slice(); });
  dns['nameserver-policy'] = policy;
  // This installation uses blacklist mode; keep existing fake-IP exclusions.
  const filterMode = dns['fake-ip-filter-mode'] || 'blacklist';
  if (filterMode === 'blacklist') {
    const filter = dns['fake-ip-filter'] || [];
    domains.forEach(function (domain) {
      const item = '+.' + domain;
      if (filter.indexOf(item) === -1) filter.push(item);
    });
    dns['fake-ip-filter'] = filter;
  }
  config.dns = dns;
  return config;
}
