// Betting board: full American layout — stacked 00/0 column, numbers, splits,
// streets, corners, six lines, top line, dozens, columns, even-money bets.
//
// In-between (split/street/corner/six-line) zones are per-cell edge overlays,
// so they always align with the cells regardless of layout or window size.
// Left-click adds the selected chip, right-click (or shift-click) removes.
// Every player's chips are rendered in that player's color so spectators can
// tell who is betting where.

const RED = new Set([1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36]);

function colorOf(n: number): string {
  if (n === 0 || n === 37) return "#0f9d58";
  return RED.has(n) ? "#c0392b" : "#1b1b1f";
}

/** Maps a cell colour to a CSS class (so gradients can be applied). */
function colorClass(color: string | undefined): string {
  if (color === "#c0392b") return "red";
  if (color === "#0f9d58") return "green";
  if (color === "#1b1b1f") return "black";
  return "dark";
}

/** One visible chip: a player's stake on one bet target. */
export interface BetChip {
  target: string;
  amount: number;
  color: string;
  playerId?: string;
  name?: string;
  mine?: boolean;
}

export interface BoardCallbacks {
  place(target: string): void;
  remove(target: string): void;
}

export class Board {
  private root: HTMLElement;
  private cells = new Map<string, HTMLElement>();
  /** target -> chip containers (the cell's stack and/or an edge zone's stack) */
  private chipHosts = new Map<string, HTMLElement[]>();
  selectedChip = 10000;
  private cb: BoardCallbacks;

  constructor(root: HTMLElement, cb: BoardCallbacks) {
    this.root = root;
    this.cb = cb;
    this.build();
  }

  /** Replace every chip stack with the current per-player chips. */
  renderBets(chips: BetChip[]): void {
    const byTarget = new Map<string, BetChip[]>();
    for (const chip of chips) {
      const list = byTarget.get(chip.target) ?? [];
      list.push(chip);
      byTarget.set(chip.target, list);
    }

    for (const [target, hosts] of this.chipHosts) {
      const list = byTarget.get(target) ?? [];
      // Stable order: your chips first, then by player id. Amounts changing
      // never reorder the stacks, so chips don't jump around while betting.
      list.sort(
        (a, b) =>
          Number(!!b.mine) - Number(!!a.mine) ||
          (a.playerId ?? a.name ?? "").localeCompare(b.playerId ?? b.name ?? ""),
      );

      for (const host of hosts) {
        const existing = new Map<string, HTMLElement>();
        for (const child of Array.from(host.children) as HTMLElement[]) {
          const key = child.dataset.player;
          if (key) existing.set(key, child);
        }

        for (const chip of list) {
          const key = chip.playerId ?? chip.name ?? chip.color;
          let el = existing.get(key);
          if (el) {
            existing.delete(key);
            host.appendChild(el); // move into the stable order
          } else {
            el = document.createElement("div");
            el.className = "pchip new";
            el.dataset.player = key;
            host.appendChild(el);
          }

          el.classList.toggle("mine", !!chip.mine);
          el.style.setProperty("--chip", chip.color);
          el.style.color = textOn(chip.color);
          const text = formatGil(chip.amount);
          if (el.textContent !== text) el.textContent = text;
          el.title = `${chip.name ?? "Player"}: ${chip.amount.toLocaleString()} gil`;
        }

        for (const leftover of existing.values()) leftover.remove();
      }
    }
  }

  private host(target: string): HTMLElement {
    const el = document.createElement("div");
    el.className = "chipstack";
    this.rootChipHosts(target, el);
    return el;
  }

  private rootChipHosts(target: string, host: HTMLElement): void {
    const list = this.chipHosts.get(target) ?? [];
    list.push(host);
    this.chipHosts.set(target, list);
  }

  private build(): void {
    this.root.innerHTML = "";
    // grid: col 1 = zero column (00/0), cols 2..13 = numbers, col 14 = column bets
    this.root.style.display = "grid";
    this.root.style.gridTemplateColumns = "minmax(52px,0.75fr) repeat(12, 1fr) minmax(44px,0.55fr)";
    this.root.style.gridTemplateRows = "repeat(3, 1fr) 0.85fr 0.85fr";
    this.root.style.gap = "8px";

    // zero column: 00 on top, 0 on bottom — both green (American table style)
    const zeroWrap = document.createElement("div");
    zeroWrap.className = "zerocol";
    zeroWrap.style.gridColumn = "1";
    zeroWrap.style.gridRow = "1 / span 3";
    zeroWrap.style.display = "flex";
    zeroWrap.style.flexDirection = "column";
    zeroWrap.style.gap = "8px";
    this.root.appendChild(zeroWrap);

    const makeZero = (target: string, label: string) => {
      const el = document.createElement("div");
      el.className = "cell zero green";
      el.dataset.target = target;
      el.style.flex = "1 1 0";
      const lbl = document.createElement("span");
      lbl.className = "label";
      lbl.textContent = label;
      el.appendChild(lbl);
      const chip = this.host(target);
      el.appendChild(chip);
      el.addEventListener("click", (e) => {
        if (e.shiftKey) this.cb.remove(target); else this.cb.place(target);
      });
      el.addEventListener("contextmenu", (e) => {
        e.preventDefault();
        this.cb.remove(target);
      });
      zeroWrap.appendChild(el);
      this.cells.set(target, el);
      return el;
    };
    makeZero("n-37", "00");
    makeZero("n-0", "0");

    // numbers: top row = 3,6,...,36
    for (let row = 1; row <= 3; row++) {
      for (let col = 1; col <= 12; col++) {
        const n = col * 3 - (row - 1);
        const el = this.cell(`n-${n}`, String(n), colorOf(n), col + 1, row);
        el.addEventListener("contextmenu", (e) => e.preventDefault());
      }
    }

    // column bets ("2 to 1") right of each row
    for (let row = 1; row <= 3; row++) {
      const el = this.cell(`col-${4 - row}`, "2:1", "#20242c", 14, row);
      el.classList.add("outside");
      el.addEventListener("contextmenu", (e) => e.preventDefault());
    }

    // dozens
    this.cell("dozen-1", "1st 12", "#20242c", 2, 4, 4);
    this.cell("dozen-2", "2nd 12", "#20242c", 6, 4, 4);
    this.cell("dozen-3", "3rd 12", "#20242c", 10, 4, 4);

    // even money — six uniform boxes, like a modern online-casino layout
    this.cell("low", "1-18", "#20242c", 2, 5, 2);
    this.cell("even", "EVEN", "#20242c", 4, 5, 2);
    this.cell("red", "RED", "#c0392b", 6, 5, 2);
    this.cell("black", "BLACK", "#1b1b1f", 8, 5, 2);
    this.cell("odd", "ODD", "#20242c", 10, 5, 2);
    this.cell("high", "19-36", "#20242c", 12, 5, 2);

    // top line under the zeros
    const tl = this.cell("topline", "TOP", "#20242c", 1, 4, 1);
    tl.title = "Top line 0-00-1-2-3 (6:1)";

    // per-cell edge zones for splits/streets/corners/six-lines
    this.buildCellEdges();
  }

  private cell(target: string, label: string, color: string | undefined, col: number, row: number, colSpan = 1, rowSpan = 1): HTMLElement {
    const el = document.createElement("div");
    el.className = "cell";
    el.dataset.target = target;
    el.classList.add(colorClass(color));
    el.style.gridColumn = `${col} / span ${colSpan}`;
    el.style.gridRow = `${row} / span ${rowSpan}`;

    const lbl = document.createElement("span");
    lbl.className = "label";
    lbl.textContent = label;
    el.appendChild(lbl);

    const chip = this.host(target);
    el.appendChild(chip);

    el.addEventListener("click", (e) => {
      if (e.shiftKey) this.cb.remove(target); else this.cb.place(target);
    });
    el.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      this.cb.remove(target);
    });

    this.root.appendChild(el);
    this.cells.set(target, el);
    return el;
  }

  /** Per-cell edge overlays: right edge = horizontal split, bottom edge =
   *  vertical split (top/middle rows) or street (bottom row), bottom-right
   *  corner = corner (top/middle rows) or six line (bottom row). */
  private buildCellEdges(): void {
    for (let row = 1; row <= 3; row++) {
      for (let col = 1; col <= 12; col++) {
        const n = col * 3 - (row - 1);
        const cell = this.cells.get(`n-${n}`)!;
        cell.style.position = "relative";

        const zone = (target: string, title: string, css: Partial<CSSStyleDeclaration>) => {
          const z = document.createElement("div");
          z.className = "cellzone";
          z.title = title;
          z.dataset.target = target;
          Object.assign(z.style, { position: "absolute", pointerEvents: "auto" }, css as any);

          const zchip = document.createElement("div");
          zchip.className = "chipstack";
          this.rootChipHosts(target, zchip);
          z.appendChild(zchip);

          z.addEventListener("click", (e) => {
            e.stopPropagation();
            if (e.shiftKey) this.cb.remove(target); else this.cb.place(target);
          });
          z.addEventListener("contextmenu", (e) => {
            e.preventDefault();
            e.stopPropagation();
            this.cb.remove(target);
          });
          cell.appendChild(z);
        };

        // right edge → split with n+3 (same row); 24px strip spans the gap
        if (col < 12) {
          zone(`split-${n}-${n + 3}`, `Split ${n}/${n + 3}`, {
            right: "-12px", top: "8px", bottom: "8px", width: "24px",
          });
        }

        // bottom edge → vertical split (inside a column) or street/six line
        // on the bottom row
        if (row < 3) {
          // split with the number below (3/2 or 2/1)
          zone(`split-${n}-${n - 1}`, `Split ${n}/${n - 1}`, {
            bottom: "-12px", left: "6px", right: col < 12 ? "6px" : "-12px", height: "24px",
          });
          if (col < 12) {
            zone(`corner-${n}-${n + 3}-${n - 1}-${n + 2}`, `Corner ${n}/${n + 3}/${n - 1}/${n + 2}`, {
              bottom: "-12px", right: "-12px", width: "24px", height: "24px",
            });
          }
        } else {
          // bottom row: street under the column (1-2-3, 4-5-6, ...)
          zone(`street-${n}-${n + 1}-${n + 2}`, `Street ${n}/${n + 1}/${n + 2}`, {
            bottom: "-12px", left: "6px", right: col < 12 ? "6px" : "-12px", height: "24px",
          });
          if (col < 12) {
            // six line at the corner between this column and the next
            zone(`sixline-${n}-${n + 1}-${n + 2}-${n + 3}-${n + 4}-${n + 5}`, `Six line ${n}-${n + 2}/${n + 3}-${n + 5}`, {
              bottom: "-12px", right: "-12px", width: "24px", height: "24px",
            });
          }
        }
      }
    }
  }
}

/** Pick black or white chip text for a player color. */
function textOn(hex: string): string {
  const m = hex.replace("#", "");
  if (m.length !== 6) return "#fff";
  const r = parseInt(m.slice(0, 2), 16);
  const g = parseInt(m.slice(2, 4), 16);
  const b = parseInt(m.slice(4, 6), 16);
  return 0.299 * r + 0.587 * g + 0.114 * b > 150 ? "#111" : "#fff";
}

function formatGil(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n % 1_000_000 === 0 ? 0 : 1)}m`;
  if (n >= 10_000) return `${(n / 1_000).toFixed(n % 1_000 === 0 ? 0 : 1)}k`;
  return String(n);
}
