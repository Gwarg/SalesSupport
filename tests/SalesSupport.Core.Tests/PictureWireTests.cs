using SalesSupport.Core.Model;
using SalesSupport.Core.Serialization;

namespace SalesSupport.Core.Tests;

/// <summary>
/// The panel is the one consumer that deserializes a CustomerPicture (PictureUpdated over
/// the hub). System.Text.Json silently skips collections without a setter, which would
/// leave the Kundbild showing only the company name — this pins the wire contract.
/// </summary>
public class PictureWireTests
{
    // Excerpt of the picture the backend stored for the first recorded demo call.
    private const string StoredPicture = """
        {"schema_version":1,
         "company":{"name":"Nordic e-Drive AB","location_hint":"Västerås","source":"call"},
         "facts":[{"id":"f1","category":"pain","text":"Svårt att korrelera CAN FD-styrkommandon med transienter på switcharna","source":"call","confidence":"high","turn":77},
                  {"id":"f2","category":"timeline","text":"Önskar leverans i oktober inför karakteriseringen i november","source":"call","confidence":"high","turn":47}],
         "threads":[{"id":"t1","topic":"Uppbyggnad av inverterlabb","kind":"discovery","status":"addressed","salience":"medium","note":"Kunden vill ta med option F2","turn":77},
                    {"id":"t12","topic":"Budget och prisoro","kind":"objection","status":"open","salience":"high","note":"Demo planeras till v. 40","turn":81}],
         "product_interest":[{"id":"p1","name_as_said":"DLM5000HD","stance":"interested","reason":"Intresserad av 12 bitars upplösning","source":"call","turn":13},
                             {"id":"p6","name_as_said":"WT1800","stance":"owns","reason":"Lånar från motorlabbet","source":"call","turn":53}],
         "action_items":[{"id":"a1","text":"Skicka offert på DLM5058HD","owner":"rep","source":"call","turn":86}]}
        """;

    [Fact]
    public void Stored_picture_deserializes_with_all_collections_populated()
    {
        var picture = JsonDefaults.Deserialize<CustomerPicture>(StoredPicture);

        Assert.Equal("Nordic e-Drive AB", picture.Company!.Name);
        Assert.Equal(2, picture.Facts.Count);
        Assert.Equal(2, picture.Threads.Count);
        Assert.Equal(2, picture.ProductInterest.Count);
        Assert.Single(picture.ActionItems);
        Assert.Equal(ThreadKind.Objection, picture.Threads[1].Kind);
        Assert.Equal(Stance.Owns, picture.ProductInterest[1].Stance);
        Assert.Equal(ActionOwner.Rep, picture.ActionItems[0].Owner);
    }

    [Fact]
    public void Picture_round_trips_through_the_shared_serializer()
    {
        var original = JsonDefaults.Deserialize<CustomerPicture>(StoredPicture);
        var again = JsonDefaults.Deserialize<CustomerPicture>(JsonDefaults.Serialize(original));

        Assert.Equal(JsonDefaults.Serialize(original), JsonDefaults.Serialize(again));
        Assert.Equal(2, again.Facts.Count);
    }
}
