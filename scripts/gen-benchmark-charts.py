#!/usr/bin/env python3
"""Generate one bar chart per optimization for ModDB."""
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np
import os

out_dir = os.path.join(os.path.dirname(__file__), '..', 'docs', 'benchmarks')
os.makedirs(out_dir, exist_ok=True)

benchmarks = [
    {
        "name": "Wind Calculations",
        "file": "weather-wind",
        "desc": "75% less CPU work per frame",
        "labels": ["60 frames (1 second)"],
        "vanilla": [4078],
        "optimum": [1029],
        "unit": "ns",
    },
    {
        "name": "Ambient Sound Updates",
        "file": "ambient-sound",
        "desc": "72-78% less CPU work when standing still",
        "labels": ["3 sounds", "6 sounds", "12 sounds"],
        "vanilla": [639, 1467, 3003],
        "optimum": [182, 303, 672],
        "unit": "ns",
    },
    {
        "name": "Collision Detection",
        "file": "block-accessor",
        "desc": "48-52% less CPU work per entity",
        "labels": ["1 entity", "10 entities", "50 entities", "200 entities"],
        "vanilla": [176, 1831, 9797, 51501],
        "optimum": [92, 876, 4907, 29741],
        "unit": "ns",
    },
    {
        "name": "Particle System",
        "file": "ticking-blocks",
        "desc": "57-59% less CPU, 99.9% less memory waste",
        "labels": ["100 blocks", "500 blocks", "1000 blocks"],
        "vanilla": [707, 3703, 7075],
        "optimum": [301, 1447, 2868],
        "unit": "ns",
        "alloc_vanilla": [3200, 16000, 32000],
        "alloc_optimum": [32, 32, 32],
    },
    {
        "name": "Block Animations (forges, querns)",
        "file": "anim-block-lod",
        "desc": "54-59% less CPU at distance",
        "labels": ["10 blocks", "50 blocks", "200 blocks"],
        "vanilla": [65622, 328809, 1302315],
        "optimum": [30454, 135169, 530736],
        "unit": "ns",
    },
    {
        "name": "Entity Visibility Checks",
        "file": "entity-shadow-cull",
        "desc": "11-29% less CPU per entity",
        "labels": ["50 entities", "200 entities", "500 entities"],
        "vanilla": [169, 656, 1709],
        "optimum": [120, 571, 1526],
        "unit": "ns",
    },
    {
        "name": "Entity Push Physics",
        "file": "repulse-agents",
        "desc": "21-25% less CPU per tick",
        "labels": ["50 entities", "200 entities", "500 entities"],
        "vanilla": [98, 413, 1020],
        "optimum": [77, 314, 766],
        "unit": "ns",
    },
    {
        "name": "Dynamic Light Search",
        "file": "dynamic-light",
        "desc": "16-17% less CPU per frame",
        "labels": ["View Distance 128", "View Distance 256", "View Distance 512"],
        "vanilla": [800, 790, 792],
        "optimum": [675, 654, 654],
        "unit": "ns",
    },
]

def format_val(v, unit):
    if v >= 1_000_000:
        return f"{v/1_000_000:.1f} ms"
    if v >= 1000:
        return f"{v/1000:.1f} \u00b5s"
    return f"{v:.0f} ns"

for b in benchmarks:
    n = len(b["labels"])
    fig, ax = plt.subplots(figsize=(max(6, n * 2.2), 4.5))

    x = np.arange(n)
    width = 0.35

    bars1 = ax.bar(x - width/2, b["vanilla"], width, label='Vanilla Client', color='#e74c3c', edgecolor='#2c3e50', linewidth=0.5)
    bars2 = ax.bar(x + width/2, b["optimum"], width, label='Optimum', color='#2ecc71', edgecolor='#2c3e50', linewidth=0.5)

    for bar, val in zip(bars1, b["vanilla"]):
        ax.text(bar.get_x() + bar.get_width()/2, bar.get_height(),
                format_val(val, b["unit"]), ha='center', va='bottom', fontsize=8, color='#c0392b')
    for bar, val in zip(bars2, b["optimum"]):
        ax.text(bar.get_x() + bar.get_width()/2, bar.get_height(),
                format_val(val, b["unit"]), ha='center', va='bottom', fontsize=8, color='#27ae60')

    for i in range(n):
        pct = (1 - b["optimum"][i] / b["vanilla"][i]) * 100
        ax.text(x[i], max(b["vanilla"][i], b["optimum"][i]) * 1.12,
                f"-{pct:.0f}%", ha='center', va='bottom', fontsize=10, fontweight='bold', color='#2c3e50')

    ax.set_ylabel('Time (lower = better)', fontsize=10)
    ax.set_title(f'{b["name"]}\n{b["desc"]}', fontsize=12, fontweight='bold')
    ax.set_xticks(x)
    ax.set_xticklabels(b["labels"], fontsize=9)
    ax.legend(fontsize=9, loc='upper left')
    ax.spines['top'].set_visible(False)
    ax.spines['right'].set_visible(False)
    ax.grid(axis='y', alpha=0.3)
    ax.set_ylim(0, max(b["vanilla"]) * 1.3)
    plt.tight_layout()
    plt.savefig(os.path.join(out_dir, f'{b["file"]}.png'), dpi=150, bbox_inches='tight', transparent=True)
    plt.close()

    if "alloc_vanilla" in b:
        fig, ax = plt.subplots(figsize=(max(6, n * 2.2), 4))
        bars1 = ax.bar(x - width/2, b["alloc_vanilla"], width, label='Vanilla Client', color='#e74c3c', edgecolor='#2c3e50', linewidth=0.5)
        bars2 = ax.bar(x + width/2, b["alloc_optimum"], width, label='Optimum', color='#2ecc71', edgecolor='#2c3e50', linewidth=0.5)
        ax.set_ylabel('Memory wasted per tick (bytes)', fontsize=10)
        ax.set_title('Particle System\n99.9% less memory waste (fewer stutters)', fontsize=12, fontweight='bold')
        ax.set_xticks(x)
        ax.set_xticklabels(b["labels"], fontsize=9)
        ax.legend(fontsize=9)
        ax.set_yscale('log')
        ax.spines['top'].set_visible(False)
        ax.spines['right'].set_visible(False)
        ax.grid(axis='y', alpha=0.3)
        plt.tight_layout()
        plt.savefig(os.path.join(out_dir, f'{b["file"]}-gc.png'), dpi=150, bbox_inches='tight', transparent=True)
        plt.close()

print(f"Generated {len(benchmarks) + 1} charts in {out_dir}/")
