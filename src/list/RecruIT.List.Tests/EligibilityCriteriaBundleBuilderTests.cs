using System.Text;
using Hl7.Fhir.Model;
using RecruIT.List.Models;
using RecruIT.List.Services.Fhir;
using FhirList = Hl7.Fhir.Model.List;
using ListFhirConstants = RecruIT.List.Services.Fhir.FhirConstants;

namespace RecruIT.List.Tests;

public sealed class EligibilityCriteriaBundleBuilderTests
{
    private static ResearchStudyDraft NewStudy(bool createEmptyScreeningList = false) =>
        new()
        {
            Title = "Heart Failure Registry",
            Acronym = "HFR",
            Description = "  ",
            CreateEmptyScreeningList = createEmptyScreeningList,
        };

    private static CriterionDraft NewCriterion(string displayText, string sql = "SELECT 1") =>
        new()
        {
            DisplayText = displayText,
            Sql = sql,
            Dialect = SqlDialect.DuckDb,
        };

    private static T Single<T>(Bundle bundle)
        where T : Resource => Assert.Single(bundle.Entry.Select(e => e.Resource).OfType<T>());

    [Fact]
    public void A_new_study_enrolls_a_group_of_its_criteria()
    {
        var reused = new CriterionDraft
        {
            DisplayText = "Adults",
            ExistingLibraryReference = " Library/common-age-min-18 ",
            Exclude = false,
        };
        var authored = NewCriterion("No prior heart transplant");
        authored.Exclude = true;

        var bundle = EligibilityCriteriaBundleBuilder.Build(NewStudy(), [reused, authored]);

        Assert.Equal(Bundle.BundleType.Transaction, bundle.Type);
        Assert.Equal(
            ["ResearchStudy", "Group", "Library"],
            bundle.Entry.Select(e => e.Resource!.TypeName)
        );
        Assert.All(
            bundle.Entry,
            e =>
            {
                Assert.Equal(Bundle.HTTPVerb.PUT, e.Request!.Method);
                Assert.Equal($"{e.Resource!.TypeName}/{e.Resource.Id}", e.Request!.Url);
            }
        );

        var study = Single<ResearchStudy>(bundle);
        var group = Single<Group>(bundle);
        var library = Single<Library>(bundle);
        Assert.Equal("Heart Failure Registry", study.Title);
        Assert.Equal("HFR", study.GetStudyAcronym());
        Assert.Null(study.Description);
        Assert.Equal([$"Group/{group.Id}"], study.Enrollment.Select(r => r.Reference));

        Assert.Collection(
            group.Characteristic,
            c =>
            {
                Assert.Equal("Adults", c.Code!.Text);
                Assert.Equal("Library/common-age-min-18", ((ResourceReference)c.Value).Reference);
                Assert.False(c.Exclude);
            },
            c =>
            {
                Assert.Equal("No prior heart transplant", c.Code!.Text);
                Assert.Equal($"Library/{library.Id}", ((ResourceReference)c.Value).Reference);
                Assert.True(c.Exclude);
            }
        );
    }

    [Fact]
    public void A_new_criterion_becomes_a_sql_query_library()
    {
        var bundle = EligibilityCriteriaBundleBuilder.Build(
            NewStudy(),
            [NewCriterion("Ejection fraction below 40%", "SELECT patient_id FROM ef")]
        );

        var library = Single<Library>(bundle);
        Assert.Equal("Ejection fraction below 40%", library.Title);
        Assert.Equal("ejection-fraction-below-40", library.Name);
        Assert.Equal(PublicationStatus.Active, library.Status);
        Assert.Contains(
            library.Type.Coding,
            c =>
                c.System == EligibilityCriteriaBundleBuilder.SqlQueryLibraryTypeSystem
                && c.Code == EligibilityCriteriaBundleBuilder.SqlQueryLibraryTypeCode
        );
        // The dialect-agnostic content first (what GetSqlText reads back), then the dialect's own.
        Assert.Equal(
            [SqlContentType.DialectAgnostic, SqlContentType.For(SqlDialect.DuckDb)],
            library.Content.Select(c => c.ContentType)
        );
        Assert.All(
            library.Content,
            c => Assert.Equal("SELECT patient_id FROM ef", Encoding.UTF8.GetString(c.Data!))
        );
        Assert.Equal("SELECT patient_id FROM ef", library.GetSqlText());
    }

    [Fact]
    public void Resubmitting_the_same_draft_targets_the_same_resources()
    {
        string[] Urls() =>
            [
                .. EligibilityCriteriaBundleBuilder
                    .Build(NewStudy(createEmptyScreeningList: true), [NewCriterion("Adults")])
                    .Entry.Select(e => e.Request!.Url!),
            ];

        var first = Urls();

        Assert.Equal(first, Urls());
        Assert.All(first, url => Assert.Matches("^[A-Za-z]+/[0-9a-f]{64}$", url));
    }

    [Fact]
    public void An_existing_study_is_kept_as_is_apart_from_its_enrollment()
    {
        var existing = new ResearchStudy
        {
            Id = "existing-study",
            Title = "Existing",
            Enrollment = [new ResourceReference("Group/old-criteria")],
        };
        existing.Identifier.Add(new Identifier("https://example.org/trials", "TRIAL-1"));
        var draft = new ResearchStudyDraft { Title = "ignored", ExistingStudyId = existing.Id };

        var bundle = EligibilityCriteriaBundleBuilder.Build(
            draft,
            [NewCriterion("Adults")],
            existing
        );

        var study = Single<ResearchStudy>(bundle);
        var group = Single<Group>(bundle);
        Assert.Equal("existing-study", study.Id);
        Assert.Equal("Existing", study.Title);
        Assert.Equal([$"Group/{group.Id}"], study.Enrollment.Select(r => r.Reference));
        Assert.Equal(
            "existing-study",
            Assert
                .Single(
                    group.Identifier,
                    i => i.System == ListFhirConstants.UrlEligibilityGroupIdentifier
                )
                .Value
        );
        // The study handed in is the cached copy - not to be modified by building a preview.
        Assert.Equal("Group/old-criteria", Assert.Single(existing.Enrollment).Reference);
    }

    [Fact]
    public void An_existing_study_draft_needs_that_study()
    {
        var draft = new ResearchStudyDraft
        {
            Title = "ignored",
            ExistingStudyId = "existing-study",
        };

        Assert.Throws<ArgumentException>(() =>
            EligibilityCriteriaBundleBuilder.Build(draft, [], new ResearchStudy { Id = "other" })
        );
    }

    [Fact]
    public void An_empty_screening_list_takes_the_query_modules_identity_for_the_study()
    {
        var bundle = EligibilityCriteriaBundleBuilder.Build(
            NewStudy(createEmptyScreeningList: true),
            []
        );

        var study = Single<ResearchStudy>(bundle);
        var list = Single<FhirList>(bundle);
        var studyIdentifier = Assert.Single(study.Identifier).Value!;
        Assert.Empty(list.Entry);
        Assert.Equal(FhirList.ListStatus.Current, list.Status);
        Assert.Equal(
            studyIdentifier,
            Assert
                .Single(
                    list.Identifier,
                    i => i.System == ListFhirConstants.UrlScreeningListIdentifier
                )
                .Value
        );
        Assert.Equal(
            EligibilityCriteriaBundleBuilder.ComputeIdentifierHashId(
                ListFhirConstants.UrlScreeningListIdentifier,
                studyIdentifier
            ),
            list.Id
        );
        var belongsToStudy = list.GetReferenceExtension(ListFhirConstants.UrlListBelongsToStudy);
        Assert.Equal($"ResearchStudy/{study.Id}", belongsToStudy?.Reference);
        Assert.Equal("HFR", belongsToStudy?.Display);
    }

    [Fact]
    public void No_screening_list_unless_asked_for() =>
        Assert.Empty(
            EligibilityCriteriaBundleBuilder
                .Build(NewStudy(), [NewCriterion("Adults")])
                .Entry.Select(e => e.Resource)
                .OfType<FhirList>()
        );

    [Fact]
    public void New_criteria_sharing_a_title_share_one_library_entry()
    {
        var first = NewCriterion("Adults", "SELECT 1");
        var second = NewCriterion("Adults", "SELECT 2");
        second.Exclude = true;

        var bundle = EligibilityCriteriaBundleBuilder.Build(NewStudy(), [first, second]);

        var library = Single<Library>(bundle);
        Assert.Equal("SELECT 1", library.GetSqlText());
        Assert.Equal(
            [$"Library/{library.Id}", $"Library/{library.Id}"],
            Single<Group>(bundle).Characteristic.Select(c => ((ResourceReference)c.Value).Reference)
        );
        Assert.Equal([false, true], Single<Group>(bundle).Characteristic.Select(c => c.Exclude));
    }
}
