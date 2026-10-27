import unittest
from unittest.mock import patch

from lts2_agent.examples import train_stub


class TrainStubFallbackTests(unittest.TestCase):
    def fallback(self, phase, kinds):
        state = {"phase": phase}
        options = [{"kind": kind} for kind in kinds]
        with patch.object(train_stub.heuristic, "policy", return_value=[]):
            return train_stub.choose_action(state, options)

    def test_closed_shop_opens_then_empty_shop_leaves(self):
        self.assertEqual(0, self.fallback("Shop", ["OpenShop", "LeaveShop"]))
        self.assertEqual(1, self.fallback("Shop", ["CloseShop", "LeaveShop"]))

    def test_available_purchase_keeps_its_existing_index(self):
        self.assertEqual(0, self.fallback("Shop", ["BuyShopItem", "CloseShop", "LeaveShop"]))

    def test_normal_shop_keeps_purchase_and_map_exit(self):
        self.assertEqual(0, self.fallback("Shop", ["BuyShopItem", "MoveTo"]))
        self.assertEqual(0, self.fallback("Shop", ["MoveTo"]))

    def test_other_phases_keep_the_first_option(self):
        self.assertEqual(0, self.fallback("Event", ["ChooseEventOption", "ChooseEventOption"]))
        self.assertEqual(0, self.fallback("Map", ["MoveTo", "MoveTo"]))

    def test_combat_ranking_takes_precedence(self):
        state = {"phase": "Combat"}
        options = [{"kind": "PlayCard"}, {"kind": "EndTurn"}]
        with patch.object(train_stub.heuristic, "policy", return_value=[(0, 2), (1, 5)]) as policy:
            self.assertEqual(1, train_stub.choose_action(state, options))
            policy.assert_called_once_with(state, options)


if __name__ == "__main__":
    unittest.main()
