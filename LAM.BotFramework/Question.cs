﻿using Microsoft.Azure.Search;
using Microsoft.Azure.Search.Models;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Connector;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using System.Text;
using LAM.BotFramework.Entities;
using LAM.BotFramework.ServiceConnectors;
using LAM.BotFramework.Helpers;

namespace LAM.BotFramework
{
    [Serializable]
    public partial class Question
    {
        #region Properties
        [NonSerialized]
        IDialogContext _context;
        [NonSerialized]
        public string LogToken = "";
        public string RetryPrompt { get; set; }
        public int CurrentQuestion
        {
            get
            {
                int currentStepID;
                _context.PrivateConversationData.TryGetValue("CurrentQuestion", out currentStepID);
                return currentStepID;
            }
            set
            {
                _context.PrivateConversationData.SetValue("CurrentQuestion", value);
            }
        }
        public Dictionary<string, string> Properties
        {
            get
            {
                Dictionary<string, string> properties = null;
                if (_context != null)
                    _context.PrivateConversationData.TryGetValue("Properties", out properties);
                return properties;
            }
            set
            {
                _context.PrivateConversationData.SetValue("Properties", value);
            }
        }
        void PropertiesStore(string VarName, string Value)
        {
            if (VarName != "" && VarName != null)
            {
                Dictionary<string, string> properties = this.Properties;
                if (properties == null)
                {
                    properties = new Dictionary<string, string>();
                }
                properties[VarName.ToLower()] = Value;
                this.Properties = properties;
            }
        }
        public void SetScenario(string scenario)
        {
            _context.PrivateConversationData.SetValue("ScenarioData", scenario);
        }
        public List<QuestionRow> Questions()
        {
            string json = "";
            _context.PrivateConversationData.TryGetValue("ScenarioData", out json);

            return JsonConvert.DeserializeObject<List<QuestionRow>>(json);
        }
        [NonSerialized]
        public QuestionRow CurrentQuestionRow;
        #endregion

        public Question() { }
        public Question(IDialogContext context)
        {
            _context = context;
        }

        public async Task Initialize(string JSon)
        {
            await this.Initialize(JSon, null);
        }
        public async Task Initialize(string JSon, string Message)
        {
            this.LogToken = Global.ScenarioName;

            //this.LogToken = LogToken;
            StackReset("main");
            SetScenario(JSon);
            CurrentQuestion = 0;
            Properties = new Dictionary<string, string>();

            QuestionRow NextQ = JsonConvert.DeserializeObject<List<QuestionRow>>(JSon)[0];

            Load(NextQ);
            await this.Execute(_context,Message);
        }

        public async Task Execute(IDialogContext context, string Message)
        {
            Dictionary<string, string> D = this.Properties;
            if (CurrentQuestionRow.BypassNode=="Yes")
            {
                //GET VARIABLE NAME
                string value = "";
                if (D.ContainsKey(CurrentQuestionRow.NodeName.ToLower()))
                {
                    value = D[CurrentQuestionRow.NodeName.ToLower()];
                    //IF EXISTS, PROCESS IT
                    if (!string.IsNullOrEmpty(value))
                    {
                        await ProcessResponse(context, value, null);
                        return;
                    }
                }
            }

            #region KEY REPLACEMENT
            //REPLACE KEYS STATED WITH PragmaOpen and PragmaClose
            foreach (var item in D)
            {
                CurrentQuestionRow.QuestionText = ReplaceString(CurrentQuestionRow.QuestionText, Global.PragmaOpen + item.Key.ToUpper() + Global.PragmaClose, item.Value, StringComparison.CurrentCultureIgnoreCase);
                CurrentQuestionRow.Options = ReplaceString(CurrentQuestionRow.Options, Global.PragmaOpen + item.Key.ToUpper() + Global.PragmaClose, item.Value, StringComparison.CurrentCultureIgnoreCase);
                CurrentQuestionRow.NextQ = ReplaceString(CurrentQuestionRow.NextQ, Global.PragmaOpen + item.Key.ToUpper() + Global.PragmaClose, item.Value, StringComparison.CurrentCultureIgnoreCase);
            }
            CurrentQuestionRow.QuestionText = CurrentQuestionRow.QuestionText.Replace("<br>", "\n\n\n");
            #endregion

            #region TRANSLATION

            string language = GetLanguage(context);
            string PromptTranslated = CurrentQuestionRow.QuestionText;
            if (CurrentQuestionRow.LangDet == "Yes" && !string.IsNullOrEmpty(Message))
            {
                string detectedLanguage = Translator.Detect(Message);
                if (detectedLanguage != language)
                {
                    SetLanguage(context, detectedLanguage);
                }
            }
            #endregion
            PromptTranslated = Translator.Translate(CurrentQuestionRow.QuestionText, "en", language);

            //FOR TESTING:
            //string s = "[{'type':'Hero','title':'Im the points card bot, how can I help you?','subtitle':'','text':'','imageURL':'http://lambot.azurewebsites.net/Images/botman.png','action':[{'type': 'ImBack','title': 'Check Points','value': 'How many points do I have?'},{'type': 'ImBack','title': 'Redeem points','value': 'I want to redeem points'},{'type': 'ImBack','title': 'Transfer points','value': 'I want to transfer points'}]}]";
            //CurrentQuestionRow.QuestionText = s;

            #region HANDLE HERO info
            string LogPrompt = PromptTranslated;
            bool bHasHero = false;
            if (CurrentQuestionRow.QuestionText.IndexOf("{") == 0)
            {
                await HeroCardPrompt(context, language);
                LogPrompt = CurrentQuestionRow.QuestionText;
                CurrentQuestionRow.QuestionText = "";
                PromptTranslated = "";
                bHasHero = true;
            }
            if (CurrentQuestionRow.QuestionText.IndexOf("[") == 0)
            {
                await CarouselCardPrompt(context, language);
                LogPrompt = CurrentQuestionRow.QuestionText;
                CurrentQuestionRow.QuestionText = "";
                PromptTranslated = "";
                bHasHero = true;
            }
            #endregion

            if (!string.IsNullOrEmpty(Message) && (CurrentQuestionRow.BypassNode=="Yes" || CurrentQuestion==0))
            {
                #region HANDLE BYPASS
                switch (CurrentQuestionRow.QuestionType)
                {
                    case "LUIS":
                        string resultT = Message;
                        string ResultTranslated = resultT;
                        if (language != "en")
                        {
                            ResultTranslated = Translator.Translate(resultT, language, "en");
                        }

                        await ProcessResponseLUIS(context, ResultTranslated);
                        break;
                    case "QnAMaker":
                        await ProcessResponseQnAMaker(context, Message);
                        break;
                    case "Search":
                        await ProcessResponseSearch(context, Message);
                        break;
                    default:
                        await ProcessResponse(context, Message,null);
                        break;
                }
                #endregion
            }
            else
            {
                ConversationLog.Log(context, "BOT", LogPrompt, LogToken, CurrentQuestion);

                #region HANDLE QUESTIONTYPES
                switch (CurrentQuestionRow.QuestionType)
                {
                    case "API":
                        //GET
                        string json = await REST.Get(CurrentQuestionRow.QuestionText, true);

                        Dictionary<string, string> list = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);

                        //READ RETURN DICTIONARY
                        foreach (var item in list)
                        {
                            PropertiesStore(item.Key, item.Value);
                        }

                        await ProcessResponse(context, "", null);
                        break;
                    case "APIFULL":
                        //SET THE CONTEXT
                        IMessageActivity dummyReply = context.MakeMessage();
                        BotProps BP = new BotProps();
                        BP.Properties = this.Properties;
                        string st1 = this.CurrentQuestionRow.NextQ;
                        int nq = 0;
                        if (st1.IndexOf('{') > -1)
                        {
                            st1 = st1.Replace("'", "\"");
                            List<NextQuestion> LNQ = JsonConvert.DeserializeObject<List<NextQuestion>>(st1);
                            nq = LNQ[0].q;
                        }
                        else
                        {
                            nq = int.Parse(st1);
                        }

                        BP.NextQuestion = nq;
                        BP.Scenario = this.Questions();
                        //BP.ResCookie = new ConversationReference(dummyReply);
                        BP.Result = "";

                        int? NextQ = null;
                        try
                        {
                            //CALL IT
                            BP = await REST.Post(CurrentQuestionRow.QuestionText, BP);

                            Properties = BP.Properties;
                            if (BP.NextQuestion != nq)
                                NextQ = BP.NextQuestion;
                            if (BP.Scenario != null)
                                this.SetScenario(JsonConvert.SerializeObject(BP.Scenario));
                        }
                        catch (Exception)
                        {

                        }

                        await ProcessResponse(context, BP.Result, NextQ);
                        break;
                    case "SUB":
                        //ADD TO THE STACK
                        StackPush(CurrentQuestionRow.Sub, this.CurrentQuestion);
                        //MOVE TO THE FIRST OF THE SUB
                        //                    GOTO CurrentQuestionRow.options
                        int nextq = int.Parse(CurrentQuestionRow.Options);
                        //AT END OF THE SUB, RETURN TO THE STACK
                        await ProcessResponse(context, "", nextq);
                        break;
                    case "Expression":
                        string sRes = "Yes";
                        if (CurrentQuestionRow.QuestionText.IndexOf(Global.PragmaOpen) > -1 && CurrentQuestionRow.QuestionText.IndexOf(Global.PragmaClose) > -1)
                        {
                            sRes = "No";
                        }
                        else
                        {
                            var result = CSharpScript.EvaluateAsync(CurrentQuestionRow.QuestionText).Result;
                            if (result.GetType().Name == "String")
                                sRes = "Yes";
                            else
                            {
                                if ((bool)result)
                                    sRes = "Yes";
                                else
                                    sRes = "No";
                            }
                        }
                        await ProcessResponse(context, sRes, null);
                        break;
                    case "LUIS":
                        if (bHasHero)
                        {
                            context.Wait(ProcessResponseLUISBypass);
                        }
                        else
                            PromptDialog.Text(context,
                                                MessageLoopLUIS,
                                                PromptTranslated,
                                                RetryPrompt);
                        break;
                    case "Text":
                        if (bHasHero)
                        {
                            context.Wait(ProcessResponseBypass);
                        }
                        else
                            PromptDialog.Text(context,
                                            MessageLoop,
                                            PromptTranslated,
                                            RetryPrompt);
                        break;
                    case "Carousel":
                        var reply = context.MakeMessage();
                        reply.AttachmentLayout = AttachmentLayoutTypes.Carousel;
                        //type,title,subtitle,text,urlforimage,urltoopen

                        List<AttachmentCard> opat = JsonConvert.DeserializeObject<List<AttachmentCard>>(CurrentQuestionRow.Options.Replace("'", "\""));
                        IList<Attachment> CardsAttachment = new List<Attachment>();

                        foreach (AttachmentCard item in opat)
                        {
                            string actiontype = "";
                            switch (item.action.type.ToLower())
                            {
                                case "openurl":
                                    actiontype = ActionTypes.OpenUrl;
                                    break;
                                case "imback":
                                    actiontype = ActionTypes.ImBack;
                                    break;
                                default:
                                    actiontype = ActionTypes.OpenUrl;
                                    break;
                            }
                            CardAction CA = new CardAction(actiontype, item.action.title, value: item.action.value);
                            CardImage CI = new CardImage(url: item.imageURL);

                            if (item.type == "Hero")
                            {
                                CardsAttachment.Add(
                                    Bot.GetHeroCard(item.title, item.subtitle, item.text, new List<CardImage>() { CI }, new List<CardAction>() { CA })
                                );
                            }
                            if (item.type == "Thumbnail")
                            {
                                CardsAttachment.Add(
                                    Bot.GetThumbnailCard(item.title, item.subtitle, item.text, CI, CA)
                                );
                            }
                        }
                        reply.Attachments = CardsAttachment;
                        await context.PostAsync(reply);
                        //await ProcessResponse(context, "");
                        PromptDialog.Text(context,
                                            MessageLoop,
                                            PromptTranslated,
                                            RetryPrompt);
                        break;
                    case "QnAMaker":
                        string sJson = CurrentQuestionRow.Options.Replace("'", "\"");
                        OptionsQnAMaker OQ = JsonConvert.DeserializeObject<OptionsQnAMaker>(sJson);
                        if (string.IsNullOrEmpty(OQ.QSearch))
                        {
                            if (bHasHero)
                            {
                                context.Wait(ProcessResponseQnABypass);
                            }
                            else
                                PromptDialog.Text(context,
                                                    MessageLoopQnAMaker,
                                                    PromptTranslated,
                                                    RetryPrompt
                                                    );
                        }
                        else
                        {
                            //the QSearch should have the Pragmas by default.
                            string result = KeyReplace(Global.PragmaOpen + OQ.QSearch + Global.PragmaClose);
                            if (result.IndexOf("{") == 0)
                            {
                                LUISresultv2 LRV2 = JsonConvert.DeserializeObject<LUISresultv2>(result);
                                result = LRV2.query;
                            }
                            await ProcessResponseQnAMaker(context, result);
                        }
                        break;
                    case "Search":
                        string sJsonS = CurrentQuestionRow.Options.Replace("'", "\"");
                        OptionsSearch OS = JsonConvert.DeserializeObject<OptionsSearch>(sJsonS);
                        if (string.IsNullOrEmpty(OS.QSearch))
                        {
                            if (bHasHero)
                            {
                                context.Wait(ProcessResponseSearchBypass);
                            }
                            else
                                PromptDialog.Text(context,
                                                    MessageLoopSearch,
                                                    PromptTranslated,
                                                    RetryPrompt
                                                    );
                        }
                        else
                        {
                            string result = KeyReplace(Global.PragmaOpen + OS.QSearch + Global.PragmaClose);
                                if (result.IndexOf("{") == 0)
                                {
                                    LUISresultv2 LRV2 = JsonConvert.DeserializeObject<LUISresultv2>(result);
                                    result = LRV2.query;
                                }
                                await ProcessResponseSearch(context, result);
                        }
                        break;
                    case "Choice":
                        string[] op = CurrentQuestionRow.Options.Split(',');
                        PromptDialog.Choice(context,
                                                MessageLoop,
                                                op,
                                                PromptTranslated,
                                                RetryPrompt,
                                                promptStyle: PromptStyle.Auto);
                        break;
                    case "ChoiceAction":
                        string[] opA = CurrentQuestionRow.Options.Split(',');
                        if (CurrentQuestionRow.Options == "")
                        {
                            string st = CurrentQuestionRow.NextQ;
                            if (st != "" && st != null)
                            {
                                if (st.IndexOf('{') > -1)
                                {
                                    st = st.Replace("'", "\"");
                                    List<NextQuestion> LNQ = JsonConvert.DeserializeObject<List<NextQuestion>>(st);
                                    opA = new string[LNQ.Count];
                                    for (int i = 0; i < LNQ.Count; i++)
                                    {
                                        opA[i] = LNQ[i].intent;
                                    }
                                }
                            }

                        }

                        PromptDialog.Choice(context,
                                                MessageLoop,
                                                opA,
                                                PromptTranslated,
                                                RetryPrompt,
                                                promptStyle: PromptStyle.Auto);
                        break;
                    case "Hero":
                        var replyH = context.MakeMessage();
                        //type,title,subtitle,text,urlforimage,urltoopen
                        AttachmentHero item1 = JsonConvert.DeserializeObject<AttachmentHero>(CurrentQuestionRow.QuestionText.Replace("'", "\""));

                        List<CardAction> LCA = new List<CardAction>();
                        foreach (var actions in item1.action)
                        {
                            string actiontype = "";
                            switch (actions.type)
                            {
                                case "OpenURL":
                                    actiontype = ActionTypes.OpenUrl;
                                    break;
                                case "ImBack":
                                    actiontype = ActionTypes.ImBack;
                                    break;
                                default:
                                    actiontype = ActionTypes.ImBack;
                                    break;
                            }
                            CardAction CA = new CardAction(actiontype, actions.title, value: actions.value);
                            LCA.Add(CA);
                        }

                        replyH.Attachments = new List<Attachment>
                        {
                            Bot.GetHeroCard(item1.title, item1.subtitle, item1.text, new List<CardImage>() { new CardImage(url: item1.imageURL) }, LCA)
                        };
                        await context.PostAsync(replyH);
                        await ProcessResponse(context, "", null);
                        break;
                    case "Boolean":
                        if (bHasHero)
                        {
                            context.Wait(ProcessResponseBypass);
                        }
                        else
                            PromptDialog.Confirm(context,
                                            MessageLoop,
                                            PromptTranslated,
                                            RetryPrompt);
                        break;
                    case "Integer":
                        if (bHasHero)
                        {
                            context.Wait(ProcessResponseBypass);
                        }
                        else
                            PromptDialog.Number(context,
                                            MessageLoop,
                                            PromptTranslated,
                                            RetryPrompt);
                        break;
                    case "Message":
                        if (!bHasHero)
                        {
                            await context.PostAsync(PromptTranslated);
                        }
                        await ProcessResponse(context, "", null);
                        break;
                    case "MessageEnd":
                        if (!bHasHero)
                        {
                            await context.PostAsync(PromptTranslated);
                        }

                        context.Done(true);
                        break;
                    case "ResetAllVars":
                        this.Properties=new Dictionary<string, string>();
                        await ProcessResponse(context, "", null);
                        break;
                    default:
                        break;
                }
                #endregion
            }
        }

        private string KeyReplace(string text)
        {
            //REPLACE KEYS STATED WITH PragmaOpen and PragmaClose
            Dictionary<string, string> D = this.Properties;
            foreach (var item in D)
            {
                text = ReplaceString(text, Global.PragmaOpen + item.Key.ToUpper() + Global.PragmaClose, item.Value, StringComparison.CurrentCultureIgnoreCase);
            }
            return text;
        }
        
        #region StackManagement
        private void StackReset(string value)
        {
            List<StackItem> Stack = new List<StackItem>();
            Stack.Add(new StackItem() { sub = value, nextQ = 0 });

            string ExistingStack = JsonConvert.SerializeObject(Stack);
            _context.PrivateConversationData.SetValue("SubStack", ExistingStack);
        }
        private void StackPush(string value, int QuestionNumber)
        {
            string ExistingStack = "";
            _context.PrivateConversationData.TryGetValue("SubStack", out ExistingStack);
            List<StackItem> Stack = JsonConvert.DeserializeObject<List<StackItem>>(ExistingStack);

            Stack.Add(new StackItem() { sub = value, nextQ = QuestionNumber });

            ExistingStack = JsonConvert.SerializeObject(Stack);
            _context.PrivateConversationData.SetValue("SubStack", ExistingStack);
        }
        private int StackPop()
        {
            string ExistingStack = "";
            _context.PrivateConversationData.TryGetValue("SubStack", out ExistingStack);
            List<StackItem> Stack = JsonConvert.DeserializeObject<List<StackItem>>(ExistingStack);

            StackItem value = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            ExistingStack = JsonConvert.SerializeObject(Stack);
            _context.PrivateConversationData.SetValue("SubStack", ExistingStack);
            return value.nextQ;
        }
        #endregion

        private async Task HeroCardPrompt(IDialogContext context, string language)
        {

            //{ 'subtitle': 'aa','text': 'aa','imageURL': 'http://lambot.azurewebsites.net/Images/cardgold.png',  'action': [    {      'type': 'ImBack',      'title': 'aaa',      'value': 'bbb'    },    {      'type': 'ImBack',      'title': 'aaa2',      'value': 'bbb2'    }  ]}
            var replyH = context.MakeMessage();
            //type,title,subtitle,text,urlforimage,urltoopen
            AttachmentHero item1 = JsonConvert.DeserializeObject<AttachmentHero>(CurrentQuestionRow.QuestionText.Replace("'", "\""));

            string iti =  Translator.Translate(item1.title, "en", language);
            string isu = Translator.Translate(item1.subtitle, "en", language);
            string ite = Translator.Translate(item1.text, "en", language);

            List<CardAction> LCA = new List<CardAction>();
            foreach (var actions in item1.action)
            {
                string actiontype = "";
                switch (actions.type.ToLower())
                {
                    case "openurl":
                        actiontype = ActionTypes.OpenUrl;
                        break;
                    case "imback":
                        actiontype = ActionTypes.ImBack;
                        break;
                    default:
                        actiontype = ActionTypes.ImBack;
                        break;
                }
                string ati = Translator.Translate(actions.title, "en", language);
                string ava = Translator.Translate(actions.value, "en", language);
                CardAction CA = new CardAction(actiontype, ati, value: ava);
                LCA.Add(CA);
            }

            replyH.Attachments = new List<Attachment>
                    {
                        Bot.GetHeroCard(iti, isu, ite,new List<CardImage>() {  new CardImage(url: item1.imageURL) }, LCA)
                    };
            await context.PostAsync(replyH);
        }
        private async Task CarouselCardPrompt(IDialogContext context, string language)
        {
            var replyH = context.MakeMessage();

            AttachmentHero[] items = JsonConvert.DeserializeObject<AttachmentHero[]>(CurrentQuestionRow.QuestionText.Replace("'", "\""));
            if (items.Length>1)
                replyH.AttachmentLayout = AttachmentLayoutTypes.Carousel;

            //{ 'subtitle': 'aa','text': 'aa','imageURL': 'http://lambot.azurewebsites.net/Images/cardgold.png',  'action': [    {      'type': 'ImBack',      'title': 'aaa',      'value': 'bbb'    },    {      'type': 'ImBack',      'title': 'aaa2',      'value': 'bbb2'    }  ]}
            //type,title,subtitle,text,urlforimage,urltoopen
            List<Attachment> LA = new List<Attachment>();
            foreach (AttachmentHero item in items)
            {
                string iti = Translator.Translate(item.title, "en", language);
                string isu = Translator.Translate(item.subtitle, "en", language);
                string ite = Translator.Translate(item.text, "en", language);

                List<CardAction> LCA = new List<CardAction>();
                foreach (var actions in item.action)
                {
                    string actiontype = "";
                    switch (actions.type.ToLower())
                    {
                        case "openurl":
                            actiontype = ActionTypes.OpenUrl;
                            break;
                        case "imback":
                            actiontype = ActionTypes.ImBack;
                            break;
                        default:
                            actiontype = ActionTypes.ImBack;
                            break;
                    }
                    string ati = Translator.Translate(actions.title, "en", language);
                    string ava = Translator.Translate(actions.value, "en", language);

                    CardAction CA = new CardAction(actiontype, ati, value: ava);
                    LCA.Add(CA);
                }
                LA.Add(
                    Bot.GetHeroCard(iti, isu, ite,new List<CardImage>() {  new CardImage(url: item.imageURL) }, LCA)
                 );

            }

            replyH.Attachments = LA;
            await context.PostAsync(replyH);
        }

        #region MessageLoop
        public async Task MessageLoop(IDialogContext context, IAwaitable<string> message)
        {
            string result = await message;

            await ProcessResponse(context, result, null);
        }
        public async Task MessageLoop(IDialogContext context, IAwaitable<bool> message)
        {
            string result = (await message).ToString();

            await ProcessResponse(context, result, null);
        }
        public async Task MessageLoop(IDialogContext context, IAwaitable<long> message)
        {
            string result = (await message).ToString();

            await ProcessResponse(context, result, null);
        }
        public async Task MessageLoopLUIS(IDialogContext context, IAwaitable<string> message)
        {
            string result = await message;

            string language = GetLanguage(context);
            string ResultTranslated = Translator.Translate(result, language, "en");

            await ProcessResponseLUIS(context, ResultTranslated);
        }
        public async Task MessageLoopSearch(IDialogContext context, IAwaitable<string> message)
        {
            string result = await message;

            await ProcessResponseSearch(context, result);
        }
        public async Task MessageLoopQnAMaker(IDialogContext context, IAwaitable<string> message)
        {
            string result = await message;

            await ProcessResponseQnAMaker(context, result);
        }
        #endregion

        #region ProcessResponse
        private async Task ProcessResponseLUISBypass(IDialogContext context, IAwaitable<object> result)
        {
            Activity act = (await result) as Activity;

            string language = GetLanguage(context);
            string ResultTranslated = Translator.Translate(act.Text, language, "en");

            await ProcessResponseLUIS(context, ResultTranslated);
        }
        private async Task ProcessResponseQnABypass(IDialogContext context, IAwaitable<object> result)
        {
            Activity act = (await result) as Activity;

            string language = GetLanguage(context);
            string ResultTranslated = Translator.Translate(act.Text, language, "en");

            await ProcessResponseQnAMaker(context, ResultTranslated);
        }
        private async Task ProcessResponseSearchBypass(IDialogContext context, IAwaitable<object> result)
        {
            Activity act = (await result) as Activity;

            string language = GetLanguage(context);
            string ResultTranslated = Translator.Translate(act.Text, language, "en");

            await ProcessResponseSearch(context, ResultTranslated);
        }
        private async Task ProcessResponseBypass(IDialogContext context, IAwaitable<object> result)
        {
            Activity act = (await result) as Activity;

            string language = GetLanguage(context);
            string ResultTranslated = Translator.Translate(act.Text, language, "en");

            await ProcessResponse(context, ResultTranslated, null);
        }
        protected async Task ProcessResponse(IDialogContext context, string result, int? ForceNextQ)
        {
            Question Q = new Question(context);
            ConversationLog.Log(context, "USER", result, LogToken, Q.CurrentQuestion);
            List<QuestionRow> LQJ = Q.Questions();
            QuestionRow QJ = LQJ[Q.CurrentQuestion];

            //Store variable
            Q.PropertiesStore(QJ.NodeName, result);

            if (ForceNextQ != null && ForceNextQ != -1)
                Q.CurrentQuestion = (int)ForceNextQ;
            else
                Q.CurrentQuestion++;

            if (ForceNextQ == null)
                Q.MoveNextStep(QJ, result);

            if (Q.CurrentQuestion >= LQJ.Count)
            {
                context.Done(true);
            }
            else
            {
                //NEXT QUESTION
                Q.Load(LQJ[Q.CurrentQuestion]);
                await Q.Execute(context,null);
            }
        }
        protected async Task ProcessResponseQnAMaker(IDialogContext context, string result)
        {
            Question Q = new Question(context);
            ConversationLog.Log(context, "USER", result, LogToken, Q.CurrentQuestion);

            List<QuestionRow> LQJ = Q.Questions();
            QuestionRow QJ = LQJ[Q.CurrentQuestion];

            Q.PropertiesStore(QJ.NodeName, result);

            OptionsQnAMaker OQ = JsonConvert.DeserializeObject<OptionsQnAMaker>(QJ.Options.Replace("'", "\""));

            QnAMaker.QnAMakerResult R = ServiceConnectors.QnAMaker.Get(OQ.KBId, OQ.Key, result);

            double OQMS = 0;
            double.TryParse(OQ.MinScore, out OQMS);

            await Render.QnAMaker(context, OQ.NotFoundMessage, R, OQMS);

            Q.CurrentQuestion++;
            Q.MoveNextStep(QJ, "");
            if (Q.CurrentQuestion >= LQJ.Count)
            {
                //QDone(this, new QuestionEventArgs(-1));
                context.Done(true);
            }
            else
            {
                //QDone(this, new QuestionEventArgs(Q.CurrentQuestion));

                Q.Load(LQJ[Q.CurrentQuestion]);
                await Q.Execute(context, null);
            }
        }

        protected async Task ProcessResponseSearch(IDialogContext context, string result)
        {
            Question Q = new Question(context);
            ConversationLog.Log(context, "USER", result, LogToken, Q.CurrentQuestion);

            List<QuestionRow> LQJ = Q.Questions();
            QuestionRow QJ = LQJ[Q.CurrentQuestion];

            Q.PropertiesStore(QJ.NodeName, result);

            OptionsSearch OS = JsonConvert.DeserializeObject<OptionsSearch>(QJ.Options.Replace("'", "\""));

            SearchServiceClient serviceClient = new SearchServiceClient(OS.ServiceName, new SearchCredentials(OS.Key));
            SearchParameters parameters;

            parameters =
                new SearchParameters()
                {
                    Select = new[] { OS.FieldQ, OS.FieldA },
                    HighlightFields = new[] { OS.FieldQ }
                };

            ISearchIndexClient indexClient = serviceClient.Indexes.GetClient(OS.Index);
            DocumentSearchResult<object> searchResults = indexClient.Documents.Search<object>(result, parameters);

            await Render.Search(context, OS, searchResults);

            Q.CurrentQuestion++;
            Q.MoveNextStep(QJ, "");
            if (Q.CurrentQuestion >= LQJ.Count)
            {
                //QDone(this, new QuestionEventArgs(-1));
                context.Done(true);
            }
            else
            {
                //QDone(this, new QuestionEventArgs(Q.CurrentQuestion));

                Q.Load(LQJ[Q.CurrentQuestion]);
                await Q.Execute(context, null);
            }
        }

        protected async Task ProcessResponseLUIS(IDialogContext context, string result)
        {
            Question Q = new Question(context);
            int currentStepID = Q.CurrentQuestion;
            ConversationLog.Log(context, "USER", result, LogToken, Q.CurrentQuestion);

            List<QuestionRow> LQJ = Q.Questions();
            QuestionRow QJ = LQJ[currentStepID];


            string sURL = QJ.Options;
            string intent = "";

            string Lresult = "";
            try
            {
                Lresult = await LUIS.getLUISresult(sURL, result);

                //v2
                LUISresultv2 LRv2 = JsonConvert.DeserializeObject<LUISresultv2>(Lresult);
                if (LRv2.topScoringIntent != null)
                {
                    if (LRv2.topScoringIntent.score > 0.3)
                    {
                        intent = LRv2.topScoringIntent.intent;
                        //STORE ENTITIES
                        foreach (LUISentities item in LRv2.entities)
                        {
                            if (item.score > 0.3)
                            {
                                Q.PropertiesStore(item.type, item.entity);
                            }
                        }

                    }
                }
                result = Lresult;


                if (intent != "")
                {
                    //await context.PostAsync("Debug message - your intent was: " + intent);
                }
                else
                {
                    await context.PostAsync("No intent found.");
                    intent = "None";
                }
            }
            catch (Exception e)
            {
                await context.PostAsync("LUIS ERROR:\n" + e.InnerException.Message);
                context.Done<object>(null);
                return;
            }

            if (intent != "")
            {
                Q.PropertiesStore(QJ.NodeName, result);

                currentStepID++;
                if (currentStepID >= LQJ.Count)
                {
                    context.Done<object>(null);
                }
                else
                {
                    if (QJ.NextQ != "" && QJ.NextQ != null)
                    {
                        if (QJ.NextQ.IndexOf("[") >= 0)
                        {
                            List<NextQuestion> LNQ = JsonConvert.DeserializeObject<List<NextQuestion>>(QJ.NextQ.Replace("'", "\""));
                            foreach (NextQuestion item in LNQ)
                            {
                                if (item.intent == intent)
                                {
                                    currentStepID = item.q;
                                }
                            }
                        }
                        else
                        {
                            currentStepID = int.Parse(QJ.NextQ);
                        }
                    }

                    Q.CurrentQuestion = currentStepID;
                    Q.Load(LQJ[currentStepID]);
                    await Q.Execute(context,null);
                }
            }
        }

        void MoveNextStep(QuestionRow QJ, string result)
        {
            string st = QJ.NextQ;
            if (st != "" && st != null)
            {
                if (st.IndexOf('{') > -1)
                {
                    st = st.Replace("'", "\"");
                    List<NextQuestion> LNQ = JsonConvert.DeserializeObject<List<NextQuestion>>(st);
                    foreach (NextQuestion item in LNQ)
                    {
                        if (item.intent == result || item.intent == "")
                        {
                            this.CurrentQuestion = item.q;
                            break;
                        }
                    }
                }
                else
                {
                    try
                    {
                        this.CurrentQuestion = int.Parse(st);

                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
            }
            if (this.CurrentQuestion == -1)
            {
                int q = StackPop();
                List<QuestionRow> LQJ = this.Questions();
                QuestionRow newQJ = LQJ[q];
                MoveNextStep(newQJ, "");
            }
        }
        public void SetLanguage(IDialogContext context, string language)
        {
            context.PrivateConversationData.SetValue("CurrentLanguage", language);
        }
        public string GetLanguage(IDialogContext context)
        {
            string detectedLanguage = "";
            context.PrivateConversationData.TryGetValue("CurrentLanguage", out detectedLanguage);
            if (string.IsNullOrEmpty(detectedLanguage))
                detectedLanguage = "en";
            return detectedLanguage;
        }
        #endregion

        public void Load(QuestionRow Q)
        {
            this.RetryPrompt = "Please try again";
            this.CurrentQuestionRow = Q;
        }

        /// <summary>
        /// Replace method that enables StringComparison
        /// </summary>
        /// <param name="str"></param>
        /// <param name="oldValue"></param>
        /// <param name="newValue"></param>
        /// <param name="comparison"></param>
        /// <returns></returns>
        public static string ReplaceString(string str, string oldValue, string newValue, StringComparison comparison)
        {
            StringBuilder sb = new StringBuilder();

            int previousIndex = 0;
            int index = str.IndexOf(oldValue, comparison);
            while (index != -1)
            {
                sb.Append(str.Substring(previousIndex, index - previousIndex));
                sb.Append(newValue);
                index += oldValue.Length;

                previousIndex = index;
                index = str.IndexOf(oldValue, index, comparison);
            }
            sb.Append(str.Substring(previousIndex));

            return sb.ToString();
        }

    }

}