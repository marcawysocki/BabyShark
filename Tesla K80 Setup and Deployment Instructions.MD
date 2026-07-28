### Tesla K80 Setup and Deployment Instructions (for when the card arrives)

#### Overview
This file consolidates everything you need to install, configure, validate, and safely run an **NVIDIA Tesla K80 (24 GB)** on a Linux Mint XFCE server for ML training and Dockerized workloads. It assumes you will validate the stack on a desktop GPU first, then reproduce the validated steps on the server when the Tesla arrives.

---

#### 1. Quick summary (one‑line)
**Use the K80 as a headless compute GPU with CUDA ≤ 11.x, two dedicated PCIe power cables, a strong PSU (1000 W recommended baseline), NVIDIA drivers + NVIDIA Container Toolkit, and a separate display GPU or enabled iGPU for desktop.**

---

#### 2. Hardware checklist (before the card arrives)
- **Motherboard**: one free PCIe x16 slot for the K80.  
- **PSU**: **1000 W 80+ Gold (or better)** minimum for single K80 + modest CPU; upgrade to 1200 W+ for high‑end CPU or expansion. Must provide **at least two separate PCIe 8‑pin (6+2) cables**.  
- **Cables**: use **two dedicated PCIe power cables** from the PSU (do not use a single splitter unless PSU vendor explicitly approves).  
- **Case & cooling**: K80 is passive‑cooled — ensure strong front‑to‑rear airflow and adequate exhaust fans.  
- **Display**: K80 has no display outputs — plan to use onboard VGA (15‑pin) if CPU iGPU is available or add a cheap discrete display GPU (GT 710, Quadro P400).  
- **Risers**: only if necessary; ensure stable power and proper seating.

---

#### 3. Driver, CUDA, and compatibility notes
- **Kepler support**: Tesla K80 (Kepler) is **not supported in CUDA 12+**. Use **CUDA 11.x** (11.7 or 11.8 recommended).  
- **NVIDIA driver**: pick a driver version compatible with CUDA 11.x and Kepler (e.g., driver 515/525/535 family depending on distro). Validate with `nvidia-smi`.  
- **TorchSharp / libtorch**: if you use TorchSharp, ensure the libtorch native binaries match your CUDA version (CUDA 11.x).  
- **.NET compatibility**: Sharky is .NET 9; many libraries targeting net6/net8 work on net9, but be prepared to build RLMatrix/TorchSharp from source and retarget to net9 if needed.

---

#### 4. Host installation steps (Linux Mint XFCE) — commands and order
> **Run these on the server after the K80 is installed.** Validate on desktop first.

1. **Update and prerequisites**
```bash
sudo apt update
sudo apt install -y build-essential dkms linux-headers-$(uname -r) curl
```

2. **Install NVIDIA driver (example placeholder — choose driver matching CUDA 11.x)**
```bash
# Example: install driver 515 (adjust to recommended driver for your distro)
sudo apt install -y nvidia-driver-515
sudo reboot
```

3. **Verify GPU visible**
```bash
nvidia-smi
# Expect to see Tesla K80 listed
```

4. **Install CUDA 11.x (11.7/11.8)**
- Follow NVIDIA’s CUDA 11.x repo or runfile instructions for your distro.
- After install, verify:
```bash
nvcc --version
nvidia-smi
```

5. **Install Docker and NVIDIA Container Toolkit**
```bash
# Install Docker (if not present)
sudo apt install -y docker.io
sudo systemctl enable --now docker

# NVIDIA Container Toolkit
distribution=$(. /etc/os-release;echo $ID$VERSION_ID)
curl -s -L https://nvidia.github.io/nvidia-docker/gpgkey | sudo apt-key add -
curl -s -L https://nvidia.github.io/nvidia-docker/$distribution/nvidia-docker.list | sudo tee /etc/apt/sources.list.d/nvidia-docker.list
sudo apt update
sudo apt install -y nvidia-container-toolkit
sudo systemctl restart docker
# Test
docker run --rm --gpus all nvidia/cuda:11.7-base nvidia-smi
```

---

#### 5. Docker / container validation
- **Host check**
  - `nvidia-smi` should list the K80 and show driver version.
- **Container check**
  - `docker run --rm --gpus all nvidia/cuda:11.7-base nvidia-smi` should show the K80 inside the container.
- **Run a small CUDA workload** (e.g., `nvidia/cuda:11.7-base` with a simple sample) to confirm kernels execute.

---

#### 6. Power and cabling best practices
- **Two dedicated PCIe cables** from the PSU to the K80 (one cable per 8‑pin connector). Do **not** use a single 8→2×8 splitter unless PSU vendor confirms it’s rated.  
- **Use PSU-supplied PCIe cables** (avoid cheap third‑party splitters).  
- **Check connector temperatures** after first load; warm connectors are normal, hot is a red flag.  
- **Keep sustained load below ~80% of PSU rating** for longevity.

---

#### 7. BIOS and display configuration
- If using **onboard VGA (15‑pin)**: enable integrated graphics in BIOS and set primary display to onboard.  
- If using a **discrete display GPU**: install it in another PCIe slot and set BIOS primary display to that slot.  
- Ensure the K80 is not assigned to Xorg/Wayland (it should remain headless for compute).

---

#### 8. Sharky / RLMatrix / TorchSharp notes for server
- **Validate TorchSharp on host** with a tiny test program that prints device info before integrating into Sharky.  
- **Try NuGet first**: add RLMatrix and TorchSharp packages to a test net9 project. If runtime/native issues occur, clone and build RLMatrix/TorchSharp from source and retarget to net9.  
- **Model runtime**: use TorchSharp for inference in Sharky; ensure the same libtorch version used during training is available at inference time.  
- **Headless SC2**: run headless StarCraft II instances for multi‑env training; Docker + NVIDIA Container Toolkit is recommended.

---

#### 9. Troubleshooting checklist
- `nvidia-smi` not showing card → check PCIe seating, power cables, BIOS, and driver install.  
- Driver install fails → remove conflicting drivers, use NVIDIA runfile or distro repo per NVIDIA instructions.  
- CUDA samples fail → mismatch between driver and CUDA; ensure driver supports chosen CUDA.  
- Container cannot see GPU → ensure `nvidia-container-toolkit` installed and Docker restarted; test with `docker run --gpus all nvidia/cuda:11.7-base nvidia-smi`.  
- High temps → improve case airflow; check fan curves and ambient temperature.

---

#### 10. Safety and operational notes
- **Do not embed cloud credentials** or external MCP servers into ladder bot builds. Keep training/ops services separate.  
- **Never ship secrets** in the server image; use CI-managed secrets for cloud jobs.  
- **Monitor temps and power** during initial stress tests; passive K80 needs strong airflow.

---

#### 11. Quick validation checklist to run when the K80 is installed
- [ ] Physically installed K80 in PCIe x16 slot and connected two dedicated PCIe cables.  
- [ ] `nvidia-smi` on host shows Tesla K80.  
- [ ] Docker + NVIDIA Container Toolkit test passes: `docker run --rm --gpus all nvidia/cuda:11.7-base nvidia-smi`.  
- [ ] TorchSharp test program runs and prints device info.  
- [ ] RLMatrix sample project builds and initializes (or you can build RLMatrix from source and retarget to net9).  
- [ ] Case airflow and PSU stability validated under load.

---

#### 12. Useful commands and quick snippets
- Host GPU check:
```bash
nvidia-smi
lspci | grep -i nvidia
```
- Docker GPU test:
```bash
docker run --rm --gpus all nvidia/cuda:11.7-base nvidia-smi
```
- Basic driver reinstall (if needed):
```bash
sudo apt remove --purge '^nvidia-.*'
# then reinstall recommended driver
sudo apt install -y nvidia-driver-515
sudo reboot
```

---

#### 13. Notes for next steps (when Tesla arrives)
- Validate the exact K80 variant (power connectors and physical length).  
- Re-run the **Host installation steps** above on the server.  
- If you validated everything on desktop, replicate the same driver/CUDA/TorchSharp versions on the server to minimize surprises.  
- If you plan distributed training, prepare a shared dataset location and model checkpoint storage (NFS, S3, or similar) and ensure network bandwidth and storage IOPS are sufficient.

---

If you want, I can now:
- produce a ready‑to‑commit `TESLA_K80_SETUP.md` file and open a PR in your repo, or  
- generate the **TorchSharp test program** and the **Docker validation script** to run on the server.

I can take care of creating the Markdown file for you now.  

---

I will create the `TESLA_K80_SETUP.md` file with the content above and prepare it for your repo so you can use it next week when the card arrives.