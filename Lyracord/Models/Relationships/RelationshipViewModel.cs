using System;
using DSharpPlus.Entities;
using DSharpPlus.Enums;
using Unicord.Universal.Models.User;
using Unicord.Universal.Services;
using Windows.UI.Xaml;

namespace Unicord.Universal.Models.Relationships
{
    public class RelationshipViewModel(DiscordRelationship rel, ViewModelBase owner) 
        : ViewModelBase(owner), IEquatable<RelationshipViewModel>, IComparable<RelationshipViewModel>
    {
        private readonly DiscordRelationship rel = rel;

        public ulong Id
            => rel.Id;
        public DiscordRelationshipType Type
            => rel.RelationshipType;
        public UserViewModel User
            => new UserViewModel(rel.User, null, this);

        public bool IsIncomingRequest
            => rel.RelationshipType == DiscordRelationshipType.IncomingRequest;

        public bool IsOutgoingRequest
            => rel.RelationshipType == DiscordRelationshipType.OutgoingRequest;

        public Visibility AcceptVisibility
            => IsIncomingRequest ? Visibility.Visible : Visibility.Collapsed;

        public string RequestLabel
            => rel.RelationshipType == DiscordRelationshipType.IncomingRequest
                ? Loc.T("Friends_IncomingRequest")
                : rel.RelationshipType == DiscordRelationshipType.OutgoingRequest
                    ? Loc.T("Friends_OutgoingRequest")
                    : "";

        public int CompareTo(RelationshipViewModel other)
        {
            var name1 = User?.DisplayName;
            var name2 = other?.User?.DisplayName;

            return string.Compare(name1, name2, StringComparison.CurrentCultureIgnoreCase);
        }

        public bool Equals(RelationshipViewModel other)
        {
            return other != null && rel.Id == other.rel.Id;
        }
    }
}
