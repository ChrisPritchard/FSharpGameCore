﻿module GameCore.GameLoop

open GameCore.GameModel
open System
open System.IO
open Microsoft.Xna.Framework
open Microsoft.Xna.Framework.Graphics;
open Microsoft.Xna.Framework.Input;
open Microsoft.Xna.Framework.Audio
open Microsoft.Xna.Framework.Media

type internal Content =
| TextureAsset of Texture2D
| TextureMapAsset of Texture2D * Map<string, Rectangle>
| FontAsset of SpriteFont
| SoundAsset of SoundEffect
| MusicAsset of Song

type internal GameLoop<'TModel> (config, updateModel, getView)
    as this = 
    inherit Game()

    let mutable graphics = new GraphicsDeviceManager(this)

    let mutable assets = Map.empty<string, Content>
    let mutable whiteTexture: Texture2D = null

    let mutable keyboardInfo = { pressed = []; keysDown = []; keysUp = [] }
    let mutable currentModel: 'TModel option = None
    let mutable currentView: ViewArtifact list = []
    let mutable currentSong: Song option = None
    let mutable firstDrawComplete = false

    let mutable fps = 0
    let mutable drawCount = 0
    let mutable drawCountStart = 0.

    let mutable spriteBatch = Unchecked.defaultof<SpriteBatch>

    do 
        match config.resolution with
        | FullScreen (w,h) -> 
            graphics.PreferredBackBufferWidth <- w
            graphics.PreferredBackBufferHeight <- h
            graphics.IsFullScreen <- true
        | Windowed (w,h) -> 
            graphics.PreferredBackBufferWidth <- w
            graphics.PreferredBackBufferHeight <- h

    let updateKeyboardInfo (keyboard: KeyboardState) (existing: KeyboardInfo) =
        let pressed = keyboard.GetPressedKeys() |> Set.ofArray
        {
            pressed = pressed |> Set.toList
            keysDown = Set.difference pressed (existing.pressed |> Set.ofList) |> Set.toList
            keysUp = Set.difference (existing.pressed |> Set.ofList) pressed |> Set.toList
        }

    let getMouseInfo (mouse: MouseState) =
        {
            position = mouse.X, mouse.Y
            pressed = mouse.LeftButton = ButtonState.Pressed, mouse.RightButton = ButtonState.Pressed
        }

    let asVector2 (x,y) = new Vector2(float32 x, float32 y)
    let asRectangle (x,y,width,height) = 
        new Rectangle (x,y,width,height)

    let drawColour (spriteBatch: SpriteBatch) destRect colour = 
        spriteBatch.Draw(
            whiteTexture, asRectangle destRect, 
            Unchecked.defaultof<Nullable<Rectangle>>, colour, 0.0f, Vector2.Zero, 
            SpriteEffects.None, 0.0f)
    
    let drawImage (spriteBatch: SpriteBatch) (assetKey, destRect) colour = 
        match Map.tryFind assetKey assets with
        | Some (TextureAsset texture) -> 
            spriteBatch.Draw(
                texture, asRectangle destRect, 
                Unchecked.defaultof<Nullable<Rectangle>>, colour, 0.0f, Vector2.Zero, 
                SpriteEffects.None, 0.0f)
        | None -> sprintf "Missing asset: %s" assetKey |> failwith
        | _-> sprintf "Asset was not a Texture2D: %s" assetKey |> failwith
            
    let drawMappedImage (spriteBatch: SpriteBatch) (assetKey, mapKey, destRect) colour = 
        match Map.tryFind assetKey assets with
        | Some (TextureMapAsset (texture, map)) when map.ContainsKey mapKey -> 
            spriteBatch.Draw(
                texture, asRectangle destRect, 
                map.[mapKey] |> Nullable, colour, 0.0f, Vector2.Zero, 
                SpriteEffects.None, 0.0f)
        | Some (TextureMapAsset _) -> sprintf "Missing map key: %s in asset: %s" mapKey assetKey |> failwith
        | None -> sprintf "Missing asset: %s" assetKey |> failwith
        | _-> sprintf "Asset was not a Texture2D: %s" assetKey |> failwith
    
    let drawText (spriteBatch: SpriteBatch) (assetKey, (text:string), position, origin, scale) colour =
        let font =
            match Map.tryFind assetKey assets with
            | Some (FontAsset f) -> f
            | None -> sprintf "Missing asset: %s" assetKey |> failwith
            | _-> sprintf "Asset was not a SpriteFont: %s" assetKey |> failwith
        let position =
            match origin with
            | TopLeft -> asVector2 position
            | Centre -> 
                let size = Vector2.Divide (font.MeasureString(text), 2.f / float32 scale)
                Vector2.Subtract (asVector2 position, size)
        spriteBatch.DrawString(
            font, text, position, colour, 
            0.0f, Vector2.Zero, float32 scale, SpriteEffects.None, 0.5f)

    let playSound assetKey =
        let sound = 
            match Map.tryFind assetKey assets with
            | Some (SoundAsset s) -> s
            | None -> sprintf "Missing asset: %s" assetKey |> failwith
            | _ -> sprintf "Asset was not a SoundEffect: %s" assetKey |> failwith
        sound.Play () |> ignore

    let playMusic assetKey =
        let song = 
            match Map.tryFind assetKey assets with
            | Some (MusicAsset s) -> s
            | None -> sprintf "Missing asset: %s" assetKey |> failwith
            | _ -> sprintf "Asset was not a Song: %s" assetKey |> failwith
        match currentSong with
        | Some s when s = song -> ()
        | _ ->
            currentSong <- Some song
            MediaPlayer.Play (song)
            MediaPlayer.IsRepeating <- true

    let updateAndPrintFPS (gameTime : GameTime) fontAsset (spriteBatch: SpriteBatch) = 
        if gameTime.TotalGameTime.TotalMilliseconds - drawCountStart > 1000. then
            fps <- drawCount
            drawCountStart <- gameTime.TotalGameTime.TotalMilliseconds
            drawCount <- 0
        else
            drawCount <- drawCount + 1
        
        let position = graphics.PreferredBackBufferWidth - 20
        drawColour spriteBatch (position, 0, 20, 18) (Color.DarkSlateGray)
        drawText spriteBatch (fontAsset, sprintf "%i" fps, (position + 3, 3), TopLeft, 0.2) Color.White

    override __.LoadContent() = 
        spriteBatch <- new SpriteBatch(this.GraphicsDevice)

        whiteTexture <- new Texture2D(this.GraphicsDevice, 1, 1) 
        whiteTexture.SetData<Color> [|Color.White|]

        assets <- 
            config.assetsToLoad
            |> List.map (
                function
                | Texture (key, path) -> 
                    use stream = File.OpenRead path
                    key, Texture2D.FromStream (this.GraphicsDevice, stream) |> TextureAsset
                | TextureMap (key, texturePath, keyPath) -> 
                    use stream = File.OpenRead texturePath
                    let texture = Texture2D.FromStream (this.GraphicsDevice, stream)
                    let content = 
                        File.ReadAllLines keyPath |> Seq.skip 1 
                        |> Seq.map (fun line -> line.Split(',') |> fun s -> s.[0], new Rectangle(int s.[1], int s.[2], int s.[3], int s.[4]))
                        |> Map.ofSeq
                    key, TextureMapAsset (texture, content)
                | Font (key, path) -> 
                    key, this.Content.Load<SpriteFont> path |> FontAsset
                | Sound (key, path) -> 
                    use stream = File.OpenRead path
                    key, SoundEffect.FromStream stream |> SoundAsset
                | Song (key, path) ->
                    let uri = new Uri (path, UriKind.RelativeOrAbsolute)
                    key, Song.FromUri (key, uri) |> MusicAsset) 
            |> Map.ofList

    override __.Update(gameTime) =
        keyboardInfo <- updateKeyboardInfo (Keyboard.GetState()) keyboardInfo
        let mouseInfo = getMouseInfo (Mouse.GetState())
        let runState = { 
            elapsed = gameTime.TotalGameTime.TotalMilliseconds 
            keyboard = keyboardInfo
            mouse = mouseInfo
        }
        
        match currentModel with
        | None -> 
            currentModel <- updateModel runState currentModel
        | Some _ when firstDrawComplete ->
            currentModel <- updateModel runState currentModel
        | _ -> ()
            
        match currentModel with
        | None -> __.Exit()
        | Some model ->
            currentView <- getView runState model

    override __.Draw(gameTime) =
        firstDrawComplete <- true
        
        match config.clearColour with
        | Some c -> this.GraphicsDevice.Clear c
        | None -> ()
        
        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp)

        currentView
            |> Seq.iter (
                function 
                | Colour (d, c) -> drawColour spriteBatch d c
                | Image (a,d,c) -> drawImage spriteBatch (a,d) c
                | MappedImage (a,m,d,c) -> drawMappedImage spriteBatch (a,m,d) c
                | Text (a,t,p,o,s,c) -> drawText spriteBatch (a,t,p,o,s) c
                | SoundEffect s -> playSound s
                | Music s -> playMusic s)
        
        match config.fpsFont with
        | Some fontAsset -> updateAndPrintFPS gameTime fontAsset spriteBatch
        | None -> ()

        spriteBatch.End()