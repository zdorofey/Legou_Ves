from __future__ import annotations

import asyncio
import json
import math
import os
import re
import sys
from threading import Thread

import pygame
from bleak import BleakClient, BleakError


ADDRESS = ""
CHAR_UUID = "00002a9d-0000-1000-8000-00805f9b34fb"
DATA_FOLDER = "data"
BASE_W, BASE_H = 1366, 768

if getattr(sys, "frozen", False):
    app_dir = os.path.dirname(sys.executable)
else:
    app_dir = os.path.dirname(os.path.abspath(__file__))

CONFIG_PATH = os.path.join(app_dir, "config.json")

N_IMG_MIN_REL_SCALE = 0.35
N_IMG_MAX_REL_SCALE = 0.40
ANIM_PERIOD_MS = 2500
SCENE_TRANSITION_MS = 500

GROUP_OFFSET_X = 0
GROUP_OFFSET_Y = 0
GROUP_SCALE = 1.0
GROUP2_OFFSET_X = 0
GROUP2_OFFSET_Y = -55
GROUP2_SCALE = 1.0

BRICK_BASE_SIZE = 250
D_IMG_REL_SCALE = 0.8
MARGIN_D_BRICK = 10
MARGIN_BRICK_COUNT = 10

brick_weights = [2.3, 5.6, 8.4]
current_weight = 0.0
last_data_time = 0
weight_timeout_ms = 10000
brick_idx = -1
ble_status = "Отключено"
scene_ready = False
transition_phase = None
transition_timer = 0
rotated = False
ready_surface: pygame.Surface | None = None
ready_surface_key: tuple[int, int] | None = None


def load_config() -> None:
    global ADDRESS, rotated
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as config_file:
            config = json.load(config_file)
        configured_address = str(config.get("address", "")).strip().upper()
        if re.fullmatch(r"[0-9A-F]{2}(?::[0-9A-F]{2}){5}", configured_address):
            ADDRESS = configured_address
        else:
            ADDRESS = ""
        rotated = bool(config.get("rotated", False))
    except Exception:
        ADDRESS = ""
        rotated = False


def save_config() -> None:
    try:
        os.makedirs(app_dir, exist_ok=True)
        address_to_save = ADDRESS
        config_to_save: dict[str, object] = {}
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as config_file:
                existing_config = json.load(config_file)
            if isinstance(existing_config, dict):
                config_to_save.update(existing_config)
                existing_address = str(existing_config.get("address", "")).strip().upper()
                if re.fullmatch(r"[0-9A-F]{2}(?::[0-9A-F]{2}){5}", existing_address):
                    address_to_save = existing_address
        except (OSError, json.JSONDecodeError, AttributeError):
            pass

        config_to_save["address"] = address_to_save
        config_to_save["rotated"] = rotated
        temporary_path = CONFIG_PATH + ".tmp"
        with open(temporary_path, "w", encoding="utf-8") as config_file:
            json.dump(config_to_save, config_file, ensure_ascii=False, indent=2)
        os.replace(temporary_path, CONFIG_PATH)
    except Exception as error:
        print("Ошибка сохранения конфига:", error)


load_config()


def resource_path(relative_path: str) -> str:
    if getattr(sys, "frozen", False):
        return os.path.join(sys._MEIPASS, relative_path)
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), relative_path)


def handle_weight(_, data: bytearray) -> None:
    global current_weight, last_data_time
    if len(data) >= 3:
        current_weight = int.from_bytes(data[1:3], "little") / 200
        last_data_time = pygame.time.get_ticks()


async def connect_ble() -> None:
    global ble_status
    connected = False
    while True:
        if not ADDRESS:
            ble_status = "Нет привязки"
            await asyncio.sleep(2)
            continue
        client = BleakClient(ADDRESS, timeout=30)
        try:
            await client.connect()
            if client.is_connected:
                if not connected:
                    connected = True
                    ble_status = "Подключено"
                await client.start_notify(CHAR_UUID, handle_weight)
                # Register one notification callback per connection. Repeating
                # start_notify adds another WinRT value-changed handler each time.
                while client.is_connected:
                    await asyncio.sleep(1)
        except (BleakError, Exception):
            ble_status = "Отключено"
            connected = False

        if client.is_connected:
            await client.disconnect()
        ble_status = "Отключено"
        connected = False
        await asyncio.sleep(5)


def run_ble_thread() -> None:
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    loop.run_until_complete(connect_ble())


pygame.init()
screen = pygame.display.set_mode((0, 0), pygame.FULLSCREEN, display=0)
SW, SH = screen.get_size()
pygame.mouse.set_visible(False)
clock = pygame.time.Clock()

scale_x = SW / BASE_W
scale_y = SH / BASE_H

font_size = max(12, int(104 * scale_y))
number_font = pygame.font.Font(resource_path(os.path.join(DATA_FOLDER, "LEGO.ttf")), font_size)
status_font = pygame.font.SysFont("Arial", max(10, int(48 * scale_y)))

bg = pygame.image.load(resource_path(os.path.join(DATA_FOLDER, "0.png"))).convert()
bg = pygame.transform.smoothscale(bg, (SW, SH))

n_raw = pygame.image.load(resource_path(os.path.join(DATA_FOLDER, "n.png"))).convert_alpha()
n_orig_w, n_orig_h = n_raw.get_size()

d_raw = pygame.image.load(resource_path(os.path.join(DATA_FOLDER, "d.png"))).convert_alpha()
d_img = pygame.transform.smoothscale(
    d_raw,
    (
        int(d_raw.get_width() * D_IMG_REL_SCALE * scale_x),
        int(d_raw.get_height() * D_IMG_REL_SCALE * scale_y),
    ),
)

brick_w = int(BRICK_BASE_SIZE * scale_x)
brick_h = int(BRICK_BASE_SIZE * scale_y)
brick_imgs = []
for index in range(1, 4):
    raw_image = pygame.image.load(resource_path(os.path.join(DATA_FOLDER, f"{index}.png"))).convert_alpha()
    brick_imgs.append(pygame.transform.smoothscale(raw_image, (brick_w, brick_h)))

Thread(target=run_ble_thread, daemon=True).start()


def get_pulse_scale(now_ms: int) -> float:
    t = (now_ms % ANIM_PERIOD_MS) / ANIM_PERIOD_MS
    midpoint = (N_IMG_MIN_REL_SCALE + N_IMG_MAX_REL_SCALE) / 2
    amplitude = (N_IMG_MAX_REL_SCALE - N_IMG_MIN_REL_SCALE) / 2
    return midpoint + amplitude * math.sin(2 * math.pi * t - math.pi / 2)


def draw_not_ready(scale: float) -> None:
    width = int(n_orig_w * scale * scale_x)
    height = int(n_orig_h * scale * scale_y)
    surface = pygame.transform.smoothscale(n_raw, (width, height))
    if rotated:
        surface = pygame.transform.rotate(surface, 90)
    rect = surface.get_rect(center=(SW // 2, SH // 2))
    screen.blit(surface, rect.topleft)


def draw_ready(scale: float) -> None:
    global ready_surface, ready_surface_key
    count = int(current_weight * 1000 / brick_weights[brick_idx])
    key = (brick_idx, count)
    if ready_surface is None or ready_surface_key != key:
        count_surface = number_font.render(str(count), True, (255, 255, 255))
        group_width = max(d_img.get_width(), brick_w, count_surface.get_width())
        group_height = (
            d_img.get_height()
            + MARGIN_D_BRICK
            + brick_h
            + MARGIN_BRICK_COUNT
            + count_surface.get_height()
        )
        ready_surface = pygame.Surface((group_width, group_height), pygame.SRCALPHA)
        ready_surface.blit(d_img, ((group_width - d_img.get_width()) // 2, 0))
        ready_surface.blit(
            brick_imgs[brick_idx],
            ((group_width - brick_w) // 2, d_img.get_height() + MARGIN_D_BRICK),
        )
        ready_surface.blit(
            count_surface,
            (
                (group_width - count_surface.get_width()) // 2,
                d_img.get_height() + MARGIN_D_BRICK + brick_h + MARGIN_BRICK_COUNT,
            ),
        )
        ready_surface_key = key

    group = ready_surface

    group_scale = GROUP2_SCALE if rotated else GROUP_SCALE
    if abs(scale * group_scale - 1.0) < 0.001:
        scaled = group
    else:
        scaled_width = max(1, int(group.get_width() * scale * group_scale))
        scaled_height = max(1, int(group.get_height() * scale * group_scale))
        scaled = pygame.transform.smoothscale(group, (scaled_width, scaled_height))
    if rotated:
        scaled = pygame.transform.rotate(scaled, 90)

    offset_x = GROUP2_OFFSET_X if rotated else GROUP_OFFSET_X
    offset_y = GROUP2_OFFSET_Y if rotated else GROUP_OFFSET_Y
    pos_x = SW // 2 + offset_x - scaled_width // 2
    pos_y = SH // 2 + offset_y - scaled_height // 2
    screen.blit(scaled, (pos_x, pos_y))


running = True
while running:
    dt = clock.tick(30)

    for event in pygame.event.get():
        if event.type == pygame.QUIT:
            running = False
        elif event.type == pygame.KEYDOWN:
            if event.key == pygame.K_ESCAPE:
                running = False
            elif event.key == pygame.K_m and event.mod & pygame.KMOD_CTRL:
                pygame.display.iconify()
            elif event.key == pygame.K_1:
                rotated = not rotated

    screen.blit(bg, (0, 0))
    if ble_status != "Подключено":
        screen.blit(status_font.render("BLE", True, (255, 0, 0)), (20, 20))

    now = pygame.time.get_ticks()
    ready = now - last_data_time <= weight_timeout_ms and current_weight >= 10

    if transition_phase is None and ready != scene_ready:
        transition_phase = "out"
        transition_timer = 0

    if transition_phase == "out":
        transition_timer += dt
        ratio = min(transition_timer / SCENE_TRANSITION_MS, 1.0)
        if ratio >= 1.0:
            if ready:
                brick_idx = (brick_idx + 1) % len(brick_imgs)
            scene_ready = ready
            transition_phase = "in"
            transition_timer = 0
    elif transition_phase == "in":
        transition_timer += dt
        ratio = min(transition_timer / SCENE_TRANSITION_MS, 1.0)
        if ratio >= 1.0:
            transition_phase = None
            transition_timer = 0

    if transition_phase == "out":
        scale = 1.0 - min(transition_timer / SCENE_TRANSITION_MS, 1.0)
        if scene_ready:
            draw_ready(scale)
        else:
            draw_not_ready(get_pulse_scale(now) * scale)
    elif transition_phase == "in":
        scale = min(transition_timer / SCENE_TRANSITION_MS, 1.0)
        if scene_ready:
            draw_ready(scale)
        else:
            draw_not_ready(get_pulse_scale(now) * scale)
    elif scene_ready:
        draw_ready(1.0)
    else:
        draw_not_ready(get_pulse_scale(now))

    pygame.display.flip()

save_config()
pygame.quit()
