using System;
using System.Collections.Generic;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class BuildProgramInfoTests
    {
        private static EpgProgram MakeProgram(
            string title = "Test Show",
            string description = "",
            long startTimestamp = 1735689600L,   // 2025-01-01 00:00 UTC
            long stopTimestamp  = 1735693200L,   // 2025-01-01 01:00 UTC
            List<string> categories = null,
            string imageUrl = null,
            string subTitle = null,
            bool isLive = false,
            bool isNew = false,
            bool isPremiere = false,
            bool isPreviouslyShown = false,
            bool isPlainText = true)
        {
            return new EpgProgram
            {
                Title = title,
                Description = description,
                StartTimestamp = startTimestamp,
                StopTimestamp  = stopTimestamp,
                Categories = categories,
                ImageUrl = imageUrl,
                SubTitle = subTitle,
                IsLive = isLive,
                IsNew = isNew,
                IsPremiere = isPremiere,
                IsPreviouslyShown = isPreviouslyShown,
                IsPlainText = isPlainText,
            };
        }

        private static ProgramInfo Build(EpgProgram p, int streamId = 1, string channelId = "1")
            => XtreamTunerHost.BuildProgramInfo(p, streamId, channelId, p.Title, p.Description);

        // ── Genres null-safety (issue #12) ───────────────────────────────────

        [Fact]
        public void NullCategories_GenresIsEmptyList_NotNull()
        {
            // Genres = null causes ArgumentNullException in Emby's SetGenres → Distinct.
            var info = Build(MakeProgram(categories: null));
            Assert.NotNull(info.Genres);
            Assert.Empty(info.Genres);
        }

        [Fact]
        public void EmptyCategories_GenresIsEmptyList()
        {
            var info = Build(MakeProgram(categories: new List<string>()));
            Assert.NotNull(info.Genres);
            Assert.Empty(info.Genres);
        }

        [Fact]
        public void WithCategories_GenresMatchesCategories()
        {
            var cats = new List<string> { "Sports", "Football" };
            var info = Build(MakeProgram(categories: cats));
            Assert.Equal(cats, info.Genres);
        }

        // ── Classification ───────────────────────────────────────────────────

        [Fact]
        public void CategoryMovie_IsMovieTrue_IsSeriesFalse()
        {
            var info = Build(MakeProgram(categories: new List<string> { "Movie" }));
            Assert.True(info.IsMovie);
            Assert.False(info.IsSeries);
            Assert.False(info.IsSports);
        }

        [Fact]
        public void CategoryFilm_IsMovieTrue()
        {
            var info = Build(MakeProgram(categories: new List<string> { "film" }));
            Assert.True(info.IsMovie);
        }

        [Fact]
        public void CategorySport_IsSportsTrue_IsSeriesFalse()
        {
            var info = Build(MakeProgram(categories: new List<string> { "Sport" }));
            Assert.True(info.IsSports);
            Assert.False(info.IsSeries);
            Assert.False(info.IsMovie);
        }

        [Fact]
        public void NoCategories_IsSeriesTrue_IsMovieFalse_IsSportsFalse()
        {
            // Without categories every program defaults to IsSeries = true
            // (Emby convention: series is the catch-all type).
            var info = Build(MakeProgram(categories: null));
            Assert.True(info.IsSeries);
            Assert.False(info.IsMovie);
            Assert.False(info.IsSports);
        }

        [Fact]
        public void CategoryNews_IsNewsTrue()
        {
            var info = Build(MakeProgram(categories: new List<string> { "News" }));
            Assert.True(info.IsNews);
        }

        [Fact]
        public void CategoryKids_IsKidsTrue()
        {
            var info = Build(MakeProgram(categories: new List<string> { "Children" }));
            Assert.True(info.IsKids);
        }

        // ── SeriesId ─────────────────────────────────────────────────────────

        [Fact]
        public void Series_SeriesIdIsLowercaseTitle()
        {
            var info = Build(MakeProgram(title: "Breaking Bad", categories: null));
            Assert.Equal("breaking bad", info.SeriesId);
        }

        [Fact]
        public void Movie_SeriesIdIsNull()
        {
            var info = Build(MakeProgram(title: "Inception", categories: new List<string> { "Movie" }));
            Assert.Null(info.SeriesId);
        }

        [Fact]
        public void Sports_SeriesIdIsNull()
        {
            var info = Build(MakeProgram(title: "Grand Prix", categories: new List<string> { "Sport" }));
            Assert.Null(info.SeriesId);
        }

        // ── Metadata fields ──────────────────────────────────────────────────

        [Fact]
        public void EmptyTitle_NameBecomesUnknown()
        {
            var info = Build(MakeProgram(title: ""));
            Assert.Equal("Unknown", info.Name);
        }

        [Fact]
        public void EmptyDescription_OverviewIsNull()
        {
            var info = Build(MakeProgram(description: ""));
            Assert.Null(info.Overview);
        }

        [Fact]
        public void EmptySubTitle_EpisodeTitleIsNull()
        {
            var info = Build(MakeProgram(subTitle: ""));
            Assert.Null(info.EpisodeTitle);
        }

        [Fact]
        public void WithSubTitle_EpisodeTitleSet()
        {
            var info = Build(MakeProgram(subTitle: "Pilot"));
            Assert.Equal("Pilot", info.EpisodeTitle);
        }

        [Fact]
        public void ValidImageUrl_ImageUrlSet()
        {
            var info = Build(MakeProgram(imageUrl: "https://example.com/img.jpg"));
            Assert.Equal("https://example.com/img.jpg", info.ImageUrl);
        }

        [Fact]
        public void RelativeImageUrl_ImageUrlIsNull()
        {
            var info = Build(MakeProgram(imageUrl: "/relative/path.jpg"));
            Assert.Null(info.ImageUrl);
        }

        [Fact]
        public void LiveFlag_IsLiveTrue()
        {
            var info = Build(MakeProgram(isLive: true));
            Assert.True(info.IsLive);
        }

        [Fact]
        public void NewFlag_IsNewTrue_IsPremiereFalse()
        {
            // XMLTV <new/> must map to ProgramInfo.IsNew so Emby's
            // "record only new episodes" series rule works.
            var info = Build(MakeProgram(isNew: true));
            Assert.True(info.IsNew);
            Assert.False(info.IsPremiere);
        }

        [Fact]
        public void PremiereFlag_IsPremiereTrue_IsNewFalse()
        {
            var info = Build(MakeProgram(isPremiere: true));
            Assert.True(info.IsPremiere);
            Assert.False(info.IsNew);
        }

        [Fact]
        public void PreviouslyShownFlag_IsRepeatTrue()
        {
            var info = Build(MakeProgram(isPreviouslyShown: true));
            Assert.True(info.IsRepeat);
        }

        [Fact]
        public void Timestamps_StartDateAndEndDateSetCorrectly()
        {
            var info = Build(MakeProgram(startTimestamp: 1735689600L, stopTimestamp: 1735693200L));
            Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), info.StartDate);
            Assert.Equal(new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc), info.EndDate);
        }

        [Fact]
        public void IdContainsStreamIdAndTimestamp()
        {
            var info = Build(MakeProgram(startTimestamp: 1735689600L), streamId: 42, channelId: "42");
            Assert.Equal("xtream_epg_42_1735689600", info.Id);
        }
    }
}
