#!/usr/bin/env python3
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
ADAPTER = ROOT / "hook" / "adapters" / "kirikiri_adapter.inc"
IPC = ROOT / "include" / "voice_hook_ipc.h"


class KirikiriTextRenderLaneContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.adapter = ADAPTER.read_text(encoding="utf-8")
        cls.ipc = IPC.read_text(encoding="utf-8")

    def test_exact_lane_has_a_distinct_source_kind(self) -> None:
        self.assertIn("kTextSourceKirikiriTextRender = 6", self.ipc)
        self.assertIn("kTextSourceKirikiriTextRender", self.adapter)

    def test_only_an_advanced_owned_logical_slot_is_published(self) -> None:
        pattern = re.compile(
            r"if\(slotAdoption !== void && slotAdoption\.advanced &&\s*"
            r"logicalSlot !== void\)\s*"
            r"global\.fushiTextRenderPublish\(line, logicalSlot\.index\);"
        )
        self.assertRegex(self.adapter, pattern)

    def test_text_render_lane_keeps_message_slots_separate(self) -> None:
        self.assertIn("fushiTextRenderSlotIndex", self.adapter)
        self.assertIn("component_identity =", self.adapter)
        self.assertIn("static_cast<uint64_t>(slot_index) + 1", self.adapter)
        self.assertIn('constexpr char kHookName[] = "TextRender";', self.adapter)

    def test_tjs_queue_is_bounded_and_native_drain_is_bounded(self) -> None:
        self.assertIn("if(queue.count > 16) queue.erase(0);", self.adapter)
        self.assertIn("for (int drained = 0; drained < 8; ++drained)", self.adapter)

    def test_native_writer_uses_only_the_native_lane_partition(self) -> None:
        self.assertIn("fushi_voice_hook::kNativeThreadPreviewStart,", self.adapter)
        self.assertIn("fushi_voice_hook::kTextLaneCount, write", self.adapter)


if __name__ == "__main__":
    unittest.main()
