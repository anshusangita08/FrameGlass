# Customising rows

Optional reference for changing what each overlay row says. Edit rows on the Row details page or in `config.json`.

Every metric is its own row with its own format string, set on the Row details page or in `config.json`. **A row's default is
just its token** — a row nobody has touched shows `{app}`, not a caption and a pair of alignment
boxes. Text you add around the token in `config.json` is kept.

```
fps       {fps}          ->    141
          FPS {fps}      ->    FPS 141
```

| group | placeholders |
|---|---|
| process | `{game}` window title, `{app}` process name, `{exe}` `{api}` `{arch}` `{display}` `{displayfull}` `{driver}` `{time}` `{date}` |
| resolution | `{res}` client size, `{resnative}` panel mode, `{resscale}` panel ÷ client |
| frame times | `{fps}` `{fpsmax}` `{fpsavg}` `{fpsmin}` `{framems}` `{graph}` |
| cpu | `{cpuname}` full, `{cpus}` model only, `{cpufull}`, `{cpuload}` `{cputemp}` `{cpuclock}` `{cpupower}` `{cpufan}` |
| gpu | `{gpu}` `{gpufull}` `{gpuload}` `{gputemp}` `{gpuclock}` `{gpupower}` `{gpufan}` |
| network / disk | `{ping}` `{netup}` `{netdown}` `{diskread}` `{diskwrite}` |
| memory | `{ram}` `{ramtotal}` `{rampct}` `{vram}` `{vramtotal}` `{vrampct}` |

The memory tokens carry their unit, and `{framems}` carries "ms", so you do not add them yourself.
`{ping}` needs a host set in the General page. `{fpsmax}`, `{fpsavg}` and `{fpsmin}` are computed over
the last 10 seconds, not since startup.

## Tags

Same names RTSS uses:

| tag | effect |
|---|---|
| `<C=RRGGBB>` … `<C>` | colour from there on, then back to the default |
| `<S=150>` … `<S>` | font size in percent from there on |
| `<A=n>` … `</A>` | fixed-width box `n` characters wide; negative right-aligns |
| `<R>` | everything after it goes in the value column |
| `<G=name>` / `<G=>` | rename a cluster, or keep this row on its own line |

A row turns the hot colour — green, orange or red — when its template mentions a value over your
thresholds: `{cputemp}` / `{gputemp}` against the temperature, `{cpuload}` / `{gpuload}` against the
load. An unknown placeholder is left visible rather than silently dropped.

## Clustering

Rows whose id starts with `cpu`, `gpu`, `fps`, `frametime`, `graph`, `net` or `disk` share a line
automatically, so several metrics fit on one line and their values still form a column:

```
cpu      {cpus}        \   5600X   temp 58 °C   load 42 %   power 68 W
cputemp  temp {cputemp} >  everything after the leader lands in the value column
cpuload  load {cpuload}/
cpupower power {cpupower}/
```

The first row of a cluster is its leader: its text stays on the left and the rest join the value
column. Editing a template can never break the alignment.
