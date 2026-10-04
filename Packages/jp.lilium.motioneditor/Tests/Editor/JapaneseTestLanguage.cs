using NUnit.Framework;

namespace Lilium
{

    /// <summary>
    /// テストは日本語の画面で走らせる（S27）。メッセージの中身を日本語で確かめているテストがあり、
    /// 走らせる PC の言語の設定で結果が変わらないようにする。終わったら元の言語に戻す
    /// </summary>
    [SetUpFixture]
    public class JapaneseTestLanguage
    {
        string saved_;

        [OneTimeSetUp]
        public void SetUp ()
        {
            saved_ = LocalizationSystem.currentLanguage;
            LocalizationSystem.currentLanguage = "ja";
        }

        [OneTimeTearDown]
        public void TearDown ()
        {
            LocalizationSystem.currentLanguage = saved_;
        }
    }

}
