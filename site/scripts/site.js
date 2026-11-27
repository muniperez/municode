// Progressive enhancement for responsive popover nav, accessible tabs
// (with hidden="until-found" + beforematch sync), and clean command copy buttons.

if (typeof HTMLElement !== "undefined" && !Object.hasOwn(HTMLElement.prototype, "popover")) {
  import("https://unpkg.com/@oddbird/popover-polyfill@0.6.1/dist/popover.min.js").catch(() => {});
}

if (typeof HTMLElement !== "undefined" && !Object.hasOwn(HTMLElement.prototype, "focusGroup")) {
  import("https://unpkg.com/@microsoft/focusgroup-polyfill@0.1.3/dist/index.js")
    .then((mod) => mod.polyfill?.())
    .catch(() => {});
}

function initNavbar() {
  const header = document.querySelector(".site-header");
  const inner = header?.querySelector(".header-inner");
  const menu = document.getElementById("site-menu");
  if (!header || !inner || !menu) return;

  let lastWidth = 0;

  const syncLayout = () => {
    const currentWidth = header.clientWidth;
    const currentlyWide = header.classList.contains("is-wide");
    if (!currentlyWide && currentWidth <= lastWidth && lastWidth !== 0) {
      lastWidth = currentWidth;
      return;
    }
    lastWidth = currentWidth;

    const wasOpen = menu.matches(":where(:popover-open, .\\:popover-open)");
    header.classList.add("is-measuring", "is-wide");
    menu.removeAttribute("popover");

    const fits = inner.scrollWidth <= inner.clientWidth;
    if (!fits) {
      header.classList.remove("is-wide");
      menu.setAttribute("popover", "auto");
      if (wasOpen && typeof menu.showPopover === "function") {
        try {
          menu.showPopover();
        } catch {
          // Ignore if top-layer restoration is unsupported
        }
      }
    }

    requestAnimationFrame(() => {
      header.classList.remove("is-measuring");
    });
  };

  if (typeof ResizeObserver !== "undefined") {
    const observer = new ResizeObserver(syncLayout);
    observer.observe(header);
    observer.observe(menu);
  }
  syncLayout();
}

function initTabs() {
  for (const container of document.querySelectorAll(".tabs")) {
    const tablist = container.querySelector('[focusgroup~="tablist"], [role="tablist"]');
    if (!tablist) continue;
    if (!tablist.hasAttribute("role")) {
      tablist.setAttribute("role", "tablist");
    }

    const tabs = Array.from(tablist.querySelectorAll("button"));
    const panels = Array.from(container.querySelectorAll('[role="tabpanel"]'));
    if (!tabs.length || tabs.length !== panels.length) continue;

    for (const tab of tabs) {
      if (!tab.hasAttribute("role")) {
        tab.setAttribute("role", "tab");
      }
    }

    const selectTab = (selected, moveFocus = false) => {
      tabs.forEach((tab, index) => {
        const isSelected = tab === selected;
        tab.setAttribute("aria-selected", String(isSelected));
        tab.toggleAttribute("focusgroupstart", isSelected);
        tab.tabIndex = isSelected ? 0 : -1;

        const panel = panels[index];
        if (!panel) return;
        if (isSelected) {
          panel.hidden = false;
        } else {
          panel.setAttribute("hidden", "until-found");
        }
      });

      if (moveFocus) {
        selected.focus();
      }
      try {
        selected.scrollIntoView({ block: "nearest", inline: "nearest", container: "nearest" });
      } catch {
        selected.scrollIntoView({ block: "nearest", inline: "nearest" });
      }
    };

    const initial = tabs.find((tab) => tab.getAttribute("aria-selected") === "true") ?? tabs[0];
    selectTab(initial, false);

    for (const tab of tabs) {
      tab.addEventListener("click", () => selectTab(tab, false));
      tab.addEventListener("focus", () => {
        if (tab.getAttribute("aria-selected") !== "true") {
          selectTab(tab, false);
        }
      });
    }

    tablist.addEventListener("keydown", (event) => {
      if (Object.hasOwn(HTMLElement.prototype, "focusGroup")) return;
      const currentIndex = tabs.indexOf(document.activeElement);
      if (currentIndex === -1) return;

      let nextIndex = -1;
      if (event.key === "ArrowRight") nextIndex = (currentIndex + 1) % tabs.length;
      else if (event.key === "ArrowLeft") nextIndex = (currentIndex - 1 + tabs.length) % tabs.length;
      else if (event.key === "Home") nextIndex = 0;
      else if (event.key === "End") nextIndex = tabs.length - 1;

      if (nextIndex !== -1) {
        event.preventDefault();
        selectTab(tabs[nextIndex], true);
      }
    });

    panels.forEach((panel, index) => {
      panel.addEventListener("beforematch", () => {
        if (tabs[index]) selectTab(tabs[index], false);
      });
    });
  }
}

function extractCommands(codeElement) {
  const explicit = codeElement.dataset.copy;
  if (explicit) return explicit.trim();

  const clone = codeElement.cloneNode(true);
  for (const ignored of clone.querySelectorAll(".term-prompt, .term-out, .term-ok")) {
    ignored.remove();
  }
  return clone.textContent
    .split("\n")
    .map((line) => line.trimEnd())
    .filter((line) => line.trim().length > 0)
    .join("\n")
    .trim();
}

function initCopyButtons() {
  const liveRegion = document.getElementById("copy-status");

  for (const button of document.querySelectorAll(".copy-btn")) {
    button.addEventListener("click", async () => {
      const frame = button.closest(".code-frame");
      const code = frame?.querySelector("pre code");
      if (!code || !navigator.clipboard) return;

      try {
        await navigator.clipboard.writeText(extractCommands(code));
        const label = button.querySelector(".copy-btn-text");
        const previous = label ? label.textContent : "";
        button.dataset.copied = "true";
        if (label) label.textContent = "Copied";
        if (liveRegion) liveRegion.textContent = "Copied commands to clipboard.";

        window.setTimeout(() => {
          button.dataset.copied = "false";
          if (label) label.textContent = previous;
        }, 1800);
      } catch {
        if (liveRegion) liveRegion.textContent = "Unable to copy automatically.";
      }
    });
  }
}

initNavbar();
initTabs();
initCopyButtons();
