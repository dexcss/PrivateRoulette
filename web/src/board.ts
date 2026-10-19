// Betting board: full European layout — numbers, splits, streets, corners,
// six-lines, dozens, columns, first-four and even-money bets.
// Left-click adds the selected chip, right-click (or shift-click) removes.

const RED = new Set([1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36]);

function colorOf(n: number): string {
  if (n === 0) return "#0f9d58";
  return RED.has(n) ? "#c0392b" : "#1b1b1f";
}

export interface BoardCallbacks {
  place(target: string): void;
  remove(target: string): void;
}

interface Cell {
  target: string;
  label: string;
  color?: string;
  col: number; // grid column (1-based)
  row: number; // grid row (1-based)
  colSpan?: number;
  rowSpan?: number;
}

export class Board {
  private root: HTMLElement;
  private chipOnTarget = new Map<string, number>();
  private cells = new Map<string, HTMLElement>();
  selectedChip = 100;
  private cb: BoardCallbacks;

  constructor(root: HTMLElement, cb: BoardCallbacks) {
    this.root = root;
    this.cb = cb;
    this.build();
  }

  /** Refresh chip stacks shown on the board from the server's bet map. */
  renderBets(bets: Record<string, number>): void {
    for (const [target, el] of this.cells) {
      const chipEl = el.querySelector(".chipstack") as HTMLElement | null;
      const amt = bets[target] ?? 0;
      if (chipEl) {
        if (amt > 0) {
          chipEl.textContent = formatGil(amt);
          chipEl.style.display = "flex";
        } else {
          chipEl.style.display = "none";
        }
      }
    }
    this.chipOnTarget = new Map(Object.entries(bets));
  }

  private build(): void {
    this.root.innerHTML = "";
    // grid: col 1 = zero, cols 2..13 = numbers, col 14 = column bets ("2 to 1")
    // rows: 1..3 numbers (top row is 3-6-9...), row 4 = dozens, row 5 = even-money
    this.root.style.display = "grid";
    this.root.style.gridTemplateColumns = "minmax(34px,0.55fr) minmax(34px,0.55fr) repeat(12, 1fr) minmax(38px,0.55fr)";
    this.root.style.gridTemplateRows = "repeat(3, 1fr) 0.8fr 0.8fr";
    this.root.style.gap = "3px";

    // zero and double-zero (American) — 00 is encoded as 37 internally
    const zero = this.cell("n-0", "0", colorOf(0), 1, 1, 1, 3);
    zero.addEventListener("contextmenu", (e) => e.preventDefault());
    const dz = this.cell("n-37", "00", colorOf(37), 2, 1, 1, 3);
    dz.addEventListener("contextmenu", (e) => e.preventDefault());

    // numbers: top row = 3,6,...,36
    for (let row = 1; row <= 3; row++) {
      for (let col = 1; col <= 12; col++) {
        const n = col * 3 - (row - 1);
        const el = this.cell(`n-${n}`, String(n), colorOf(n), col + 2, row);
        el.addEventListener("contextmenu", (e) => e.preventDefault());
      }
    }

    // column bets ("2 to 1") right of each row
    for (let row = 1; row <= 3; row++) {
      const c = row; // top row is column 3
      const el = this.cell(`col-${4 - row}`, "2:1", "#20242c", 15, row);
      el.classList.add("outside");
      el.addEventListener("contextmenu", (e) => e.preventDefault());
    }

    // dozens
    this.cell("dozen-1", "1st 12", "#20242c", 3, 4, 4);
    this.cell("dozen-2", "2nd 12", "#20242c", 7, 4, 4);
    this.cell("dozen-3", "3rd 12", "#20242c", 11, 4, 4);

    // even money
    this.cell("low", "1-18", "#20242c", 3, 5, 3);
    this.cell("even", "EVEN", "#20242c", 6, 5, 2);
    this.cell("red", "RED", "#c0392b", 8, 5, 1);
    this.cell("black", "BLACK", "#1b1b1f", 9, 5, 1);
    this.cell("odd", "ODD", "#20242c", 10, 5, 2);
    this.cell("high", "19-36", "#20242c", 12, 5, 3);

    // top line under the zeros
    const tl = this.cell("topline", "0-00-1-2-3", "#20242c", 1, 4, 2);
    tl.title = "Top line (6:1)";

    // line-click zones for splits/streets/corners/sixlines overlay
    this.buildGutterZones();
  }

  private cell(target: string, label: string, color: string | undefined, col: number, row: number, colSpan = 1, rowSpan = 1): HTMLElement {
    const el = document.createElement("div");
    el.className = "cell";
    el.dataset.target = target;
    if (color) el.style.background = color;
    el.style.gridColumn = `${col} / span ${colSpan}`;
    el.style.gridRow = `${row} / span ${rowSpan}`;

    const lbl = document.createElement("span");
    lbl.className = "label";
    lbl.textContent = label;
    el.appendChild(lbl);

    const chip = document.createElement("div");
    chip.className = "chipstack";
    chip.style.display = "none";
    el.appendChild(chip);

    el.addEventListener("click", (e) => {
      if (e.shiftKey) this.cb.remove(target);
      else this.cb.place(target);
    });
    el.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      this.cb.remove(target);
    });

    this.root.appendChild(el);
    this.cells.set(target, el);
    return el;
  }

  /**
   * Overlay invisible hit zones in the gutters between number cells:
   * vertical gutter → split, horizontal gutter → street (of 3), intersection → corner.
   * Implemented with a single absolutely-positioned layer using pixel math on the grid.
   */
  private buildGutterZones(): void {
    const layer = document.createElement("div");
    layer.className = "gutters";
    layer.style.position = "absolute";
    layer.style.inset = "0";
    layer.style.pointerEvents = "none";
    this.root.style.position = "relative";
    this.root.appendChild(layer);

    const zones: { el: HTMLDivElement }[] = [];

    const mkZone = (target: string, title: string): HTMLDivElement => {
      const z = document.createElement("div");
      z.title = title;
      z.dataset.target = target;
      z.style.position = "absolute";
      z.style.pointerEvents = "auto";
      z.style.opacity = "0";
      z.addEventListener("click", (e) => {
        if (e.shiftKey) this.cb.remove(target);
        else this.cb.place(target);
      });
      z.addEventListener("contextmenu", (e) => {
        e.preventDefault();
        this.cb.remove(target);
      });
      layer.appendChild(z);
      zones.push({ el: z });
      return z;
    };

    // compute after layout
    requestAnimationFrame(() => {
      const rootRect = this.root.getBoundingClientRect();
      const numberCells: { n: number; r: DOMRect }[] = [];
      for (let row = 1; row <= 3; row++) {
        for (let col = 1; col <= 12; col++) {
          const n = col * 3 - (row - 1);
          const el = this.cells.get(`n-${n}`)!;
          numberCells.push({ n, r: el.getBoundingClientRect() });
        }
      }
      const byPos = new Map(numberCells.map((c) => [`${c.r.left.toFixed(1)}_${c.r.top.toFixed(1)}`, c]));
      const findCell = (left: number, top: number) => byPos.get(`${left.toFixed(1)}_${top.toFixed(1)}`);
      const gap = 3;

      for (const { n, r } of numberCells) {
        const w = r.width;
        const h = r.height;
        const rowOf = (n: number) => 3 - (((n - 1) % 3)); // 3..1 top→bottom => top row n%3==0
        // vertical gutter to the right neighbor (same visual row): n and n+3 → split
        const right = findCell(r.left + w + gap, r.top);
        if (right && right.n === n + 3) {
          const z = mkZone(`split-${n}-${n + 3}`, `Split ${n}/${n + 3}`);
          z.style.left = `${r.left - rootRect.left + w}px`;
          z.style.top = `${r.top - rootRect.top}px`;
          z.style.width = `${gap}px`;
          z.style.height = `${h}px`;
        }
        // horizontal gutter below: n and n-1 (row below has n-1 when same column)
        const below = findCell(r.left, r.top + h + gap);
        if (below && below.n === n - 1) {
          // street when n is the top of a column-of-3 (n % 3 == 0): covers n, n-1, n-2
          if (n % 3 === 0) {
            const z = mkZone(`street-${n}-${n - 1}-${n - 2}`, `Street ${n}-${n - 2}`);
            z.style.left = `${r.left - rootRect.left}px`;
            z.style.top = `${r.top - rootRect.top + h}px`;
            z.style.width = `${w}px`;
            z.style.height = `${gap}px`;
          } else {
            const z = mkZone(`split-${n}-${n - 1}`, `Split ${n}/${n - 1}`);
            z.style.left = `${r.left - rootRect.left}px`;
            z.style.top = `${r.top - rootRect.top + h}px`;
            z.style.width = `${w}px`;
            z.style.height = `${gap}px`;
          }
          // corner between 4 cells (needs both a right neighbor and a below neighbor)
          const belowRight = findCell(r.left + w + gap, r.top + h + gap);
          if (below && belowRight && belowRight.n === n - 1 + 3) {
            const z = mkZone(`corner-${n}-${n + 3}-${n - 1}-${n + 2}`, `Corner ${n}/${n+3}/${n-1}/${n+2}`);
            z.style.left = `${r.left - rootRect.left + w}px`;
            z.style.top = `${r.top - rootRect.top + h}px`;
            z.style.width = `${gap}px`;
            z.style.height = `${gap}px`;
          }
        }
      }

      // six lines: gutter at the bottom edge of the number grid between columns
      for (let col = 1; col <= 12; col++) {
        const topCell = numberCells.find((c) => c.n === col * 3);
        if (!topCell) continue;
        const bottomRowTop = topCell.r.top + 2 * (topCell.r.height + gap);
        const z = mkZone(`sixline-${col * 3}-${col * 3 + 1}-${col * 3 + 2}-${col * 3 - 1}-${col * 3 - 2}-${col * 3 - 3}`, `Six line ${col * 3 - 2}-${col * 3 + 1}`);
        z.style.left = `${topCell.r.left - rootRect.left}px`;
        z.style.top = `${bottomRowTop + topCell.r.height}px`;
        z.style.width = `${topCell.r.width}px`;
        z.style.height = `${gap}px`;
      }
    });
  }
}

function formatGil(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n % 1_000_000 === 0 ? 0 : 1)}m`;
  if (n >= 10_000) return `${(n / 1000).toFixed(n % 1000 === 0 ? 0 : 1)}k`;
  return String(n);
}
