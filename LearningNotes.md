# Reviving an Old Azure IoT DevKit with Copilot

<p align="center"><img src="dingdong.jpg" alt="DingDong" width="25%" /></p>

## What I built

A small desk gadget called **DingDong** that lives next to my monitor and tells me, at a glance:

- how many **emails I've been @mentioned in**, and
- how many **unread Teams chats** I have.

It also doubles as a Pomodoro timer, NTP clock, comfort meter (temperature / humidity), Wi-Fi survey, step counter, bubble level, compass, and noise meter — flip through them with the two on-board buttons.

The gadget is a **MXChip AZ3166 IoT DevKit** I had lying around from a hackathon a few years back. It has a tiny OLED, Wi-Fi, a few sensors, and two buttons — perfect for this. A Windows companion app on my PC (`ControlPlan`) reads notifications + Teams badge locally and feeds the device over HTTP every 60 seconds.

> Repo: <https://github.com/mrmubi/dingdong>

---

## What I learned

I did this as part of our **learning-day exercise**.

**The eye-opener:** Copilot did the heavy lifting — about **3 hours of agent time** iterating to a working version. My share was **under 30 minutes total** of typing prompts and steering: "no, use HttpListener not Kestrel", "split secrets out", "this binds to the wrong interface", "the firewall rule is program-scoped — make it port-scoped", etc.

Stuff that would have taken me **days** the old way got done in **hours**:

- Wiring up the AZ3166 BSP from scratch in the Arduino toolchain.
- A WinForms HTTP host with auth token, IP allowlist, and a live-discovery Security UI.
- OCR'ing the Teams taskbar badge through UI Automation + Tesseract (because New Teams doesn't emit Action Center toasts).
- Working through a real chain of Windows bugs: URL ACL "Access denied", a Windows port exclusion range hijacking 8088, firewall rules being program-scoped instead of port-scoped, the public-vs-private network profile…
- Securing the LAN: bound interface + 24-byte token + IP allowlist + URL ACL reservation, all wrapped in a Security tab.
- Resilience: when the server is unreachable the firmware falls back to a curated 9-gadget menu so the device stays useful offline.

I mostly **observed, intervened, and course-corrected**. Copilot proposed the design, wrote the code, ran the builds, flashed the firmware, diagnosed errors from the logs, and even pushed the repo.

**Bonus — the case.** In parallel I asked Copilot to generate a 3D model of an enclosure for the device and export it for my Bambu P1S. About **45 minutes later**, by the time the firmware + app were stable, the print had also finished. Hardware and software arrived at the finish line together — a first for me.

---

## What this changes for me

It's striking how much of the "engineering grunt" — boilerplate, glue code, Windows networking arcana, OCR pipelines, deployment scripts — gets compressed into prompt-and-review.

The skill is shifting from *typing the code* to:

- **Framing the problem well** ("here's what I want; here are the constraints").
- **Knowing when to intervene** ("no, that approach won't survive the BSP reset bug — drop Wi-Fi after the fetch").
- **Spotting plausible-but-wrong** suggestions before they snowball.
- **Insisting on the right defaults** for security and packaging.

Great fun, and a glimpse of what daily engineering looks like when the assistant can carry a multi-hour iteration loop on its own.

---

## Try it / read the code

- Repo, README, and full source: <https://github.com/mrmubi/dingdong>
- Hardware: MXChip AZ3166 IoT DevKit (Arduino-compatible)
- Host: Windows 10/11 + .NET 8 WinForms

Happy to demo if anyone's curious!
