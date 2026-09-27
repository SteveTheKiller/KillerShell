(function () {
  var metadata = document.querySelector(".installer-meta") || document.querySelector(".mcp-installer-meta");
  if (metadata && !metadata.querySelector(".mcp-runtime")) {
    var runtime = document.createElement("span"); runtime.className = "mcp-runtime"; runtime.textContent = ".NET 10"; metadata.insertBefore(runtime, metadata.children[2] || null);
  }

  document.querySelectorAll("#limits li").forEach(function (item) {
    if (/public installer/i.test(item.textContent || "")) item.remove();
  });
  var limitSource = document.querySelector("#limits .mcp-limit-source");
  if (limitSource) {
    limitSource.textContent = limitSource.textContent.replace(/\s*The public installer (?:is still being prepared and )?has not been released\.?/i, "");
  }

  fetch("https://api.github.com/repos/SteveTheKiller/KillerMCP/releases/latest?cache=" + Date.now(), {
    cache: "no-store",
    headers: { Accept: "application/vnd.github+json" }
  }).then(function (response) {
    if (!response.ok) throw new Error("Release lookup failed");
    return response.json();
  }).then(function (release) {
    var asset = (release.assets || []).find(function (item) { return item.name === "KillerMCP-Setup.exe"; });
    if (!asset) return;
    var meta = document.querySelector(".installer-meta") || document.querySelector(".mcp-installer-meta");
    var download = document.querySelector(".mcp-download");
    if (download) download.href = asset.browser_download_url;
    if (!meta) return;
    var values = meta.querySelectorAll("span");
    if (values[0]) values[0].textContent = "Version " + String(release.tag_name || "").replace(/^v/i, "");
    if (values[1]) values[1].textContent = (asset.size / 1048576).toFixed(1) + " MiB";
    var link = meta.querySelector("a");
    if (link) link.href = release.html_url;
  }).catch(function () {});
})();
