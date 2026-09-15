using System;

namespace YgoMaster
{
    partial class GameServer
    {
        void Act_YgoMasterUnlockAllCards(GameServerWebRequest request)
        {
            int changedCount = 0;

            foreach (int cardId in CardRare.Keys)
            {
                bool changed = false;

                foreach (
                    CardStyleRarity style in new[]
                    {
                        CardStyleRarity.Normal,
                        CardStyleRarity.Shine,
                        CardStyleRarity.Royal,
                    }
                )
                {
                    int count = request.Player.Cards.GetCount(
                        cardId,
                        PlayerCardKind.Dismantle,
                        style
                    );
                    if (count < 3)
                    {
                        request.Player.Cards.SetCount(cardId, 3, PlayerCardKind.Dismantle, style);
                        changed = true;
                    }
                }

                if (changed)
                {
                    changedCount++;
                }
            }

            SavePlayer(request.Player);
            WriteCards_have(request);

            request.Response["YgoMaster"] = new System.Collections.Generic.Dictionary<
                string,
                object
            >()
            {
                {
                    "unlock_all_cards",
                    new System.Collections.Generic.Dictionary<string, object>()
                    {
                        { "changed", changedCount },
                        { "total", CardRare.Count },
                    }
                },
            };
        }
    }
}
