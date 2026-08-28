(() => {
  const root = document.querySelector(".run");
  if (!root) return;

  const jobId = root.getAttribute("data-job");
  const pill = document.getElementById("status-pill");
  const label = document.getElementById("step-label");
  const count = document.getElementById("step-count");
  const fill = document.getElementById("bar-fill");
  const timeline = document.getElementById("timeline");
  const items = timeline ? [...timeline.querySelectorAll("li")] : [];

  async function tick() {
    let data;
    try {
      const res = await fetch(`/Job/Progress/${jobId}`, {
        headers: { Accept: "application/json" },
        cache: "no-store",
      });
      if (!res.ok) return;
      data = await res.json();
    } catch {
      window.setTimeout(tick, 1200);
      return;
    }

    if (pill) {
      pill.textContent = data.status;
      pill.className = "pill " + String(data.status || "").toLowerCase();
    }
    if (label) label.textContent = data.stepLabel;
    if (count) count.textContent = `${data.currentStep} / ${data.stepCount}`;
    if (fill) fill.style.width = `${Math.round((data.progress || 0) * 100)}%`;

    const completed = data.completedSteps || [];
    items.forEach((li, i) => {
      li.classList.remove("done", "current");
      if (i < completed.length) li.classList.add("done");
      else if (i === Math.max(0, (data.currentStep || 1) - 1)) li.classList.add("current");
    });

    if (data.isDetached) {
      window.location.href = `/Job/Detached/${jobId}`;
      return;
    }
    if (data.status === "Completed" || data.status === "Failed") {
      window.location.href = `/Job/Session/${jobId}`;
      return;
    }

    window.setTimeout(tick, 700);
  }

  tick();
})();
