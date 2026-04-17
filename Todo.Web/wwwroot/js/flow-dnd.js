// Custom drag & drop for the automation flow editor.
// Does NOT use HTML5 native drag-and-drop (which shows a transparent browser ghost).
// Instead: mousedown → clone follows cursor at 100% opacity → mouseup drops.

let state = null;
let dotNetRef = null;

export function init(ref) {
    dotNetRef = ref;
    document.addEventListener("mousedown", onMouseDown, true);
    document.addEventListener("mousemove", onMouseMove, true);
    document.addEventListener("mouseup", onMouseUp, true);
    document.addEventListener("keydown", onKey, true);
}

export function dispose() {
    document.removeEventListener("mousedown", onMouseDown, true);
    document.removeEventListener("mousemove", onMouseMove, true);
    document.removeEventListener("mouseup", onMouseUp, true);
    document.removeEventListener("keydown", onKey, true);
    dotNetRef = null;
}

function onMouseDown(e) {
    if (e.button !== 0) return;
    if (state) return;

    // Don't interfere with clicks on controls inside a node
    if (e.target.closest("button, input, select, textarea, a, label")) return;

    const src = e.target.closest("[data-dnd-source]");
    if (!src) return;

    e.preventDefault();

    const sourceSpec = src.dataset.dndSource;       // "move:condition:2" or "new:action:runClaudeSkill"
    const rect = src.getBoundingClientRect();

    // Build an opaque ghost clone that follows the cursor.
    const ghost = src.cloneNode(true);
    ghost.classList.add("flow-dnd-ghost");
    ghost.style.position = "fixed";
    ghost.style.left = rect.left + "px";
    ghost.style.top = rect.top + "px";
    ghost.style.width = rect.width + "px";
    ghost.style.height = rect.height + "px";
    ghost.style.zIndex = "9999";
    ghost.style.pointerEvents = "none";
    ghost.style.opacity = "1";
    ghost.style.boxShadow = "0 10px 28px rgba(0,0,0,0.55)";
    ghost.style.transform = "rotate(1.5deg) scale(1.02)";
    ghost.style.transition = "transform 0.08s ease";
    document.body.appendChild(ghost);

    state = {
        sourceSpec,
        srcElement: src,
        ghost,
        offsetX: e.clientX - rect.left,
        offsetY: e.clientY - rect.top,
        currentTarget: null,
        startX: e.clientX,
        startY: e.clientY,
        moved: false,
    };

    // Hide the source element visually if it's an existing node being moved.
    if (sourceSpec.startsWith("move:")) {
        src.classList.add("flow-dnd-source-hidden");
    }

    activateSlots(sourceSpec);
    document.body.classList.add("flow-dnd-active");
}

function onMouseMove(e) {
    if (!state) return;

    // Until the mouse has moved a minimum distance, don't commit to a drag (allows clicks).
    if (!state.moved) {
        const dx = e.clientX - state.startX;
        const dy = e.clientY - state.startY;
        if (dx * dx + dy * dy < 16) return;
        state.moved = true;
    }

    state.ghost.style.left = (e.clientX - state.offsetX) + "px";
    state.ghost.style.top = (e.clientY - state.offsetY) + "px";

    // Find the drop slot under the cursor.
    state.ghost.style.visibility = "hidden";
    const under = document.elementFromPoint(e.clientX, e.clientY);
    state.ghost.style.visibility = "";
    const slot = under?.closest(".flow-dnd-slot-active") ?? null;
    if (slot !== state.currentTarget) {
        state.currentTarget?.classList.remove("flow-dnd-slot-hover");
        slot?.classList.add("flow-dnd-slot-hover");
        state.currentTarget = slot;
    }
}

function onMouseUp() {
    if (!state) return;
    const { srcElement, ghost, currentTarget, sourceSpec, moved } = state;

    if (moved && currentTarget && dotNetRef) {
        const targetSpec = currentTarget.dataset.dndTarget; // "condition:3" or "trigger:0"
        dotNetRef.invokeMethodAsync("HandleFlowDrop", sourceSpec, targetSpec);
    }

    ghost.remove();
    srcElement.classList.remove("flow-dnd-source-hidden");
    deactivateSlots();
    document.body.classList.remove("flow-dnd-active");
    state = null;
}

function onKey(e) {
    if (state && e.key === "Escape") {
        state.ghost.remove();
        state.srcElement.classList.remove("flow-dnd-source-hidden");
        deactivateSlots();
        document.body.classList.remove("flow-dnd-active");
        state = null;
    }
}

function activateSlots(sourceSpec) {
    // sourceSpec formats:
    //   "move:condition:2"   — moving an existing condition node at index 2
    //   "new:trigger:interval" — dragging a new trigger from the palette
    const parts = sourceSpec.split(":");
    const isMove = parts[0] === "move";
    const kind = parts[1];
    const fromIdx = isMove ? parseInt(parts[2]) : null;

    document.querySelectorAll("[data-dnd-target]").forEach(el => {
        const t = el.dataset.dndTarget;
        const [tKind, tIdxStr] = t.split(":");
        if (tKind !== kind) return;
        const tIdx = parseInt(tIdxStr);

        // For move: hide slots immediately before/after the source position (no-op drops).
        if (isMove && (tIdx === fromIdx || tIdx === fromIdx + 1)) return;

        el.classList.add("flow-dnd-slot-active");
    });
}

function deactivateSlots() {
    document.querySelectorAll(".flow-dnd-slot-active").forEach(el => el.classList.remove("flow-dnd-slot-active"));
    document.querySelectorAll(".flow-dnd-slot-hover").forEach(el => el.classList.remove("flow-dnd-slot-hover"));
}
